/*
Copyright (C) 2009 Pierre St Juste <ptony82@ufl.edu>, University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using Brunet;
using Brunet.Applications;
using Brunet.DistributedServices;
using Brunet.Security;
using Ipop;
using Ipop.ManagedNode;

#if SVPN_NUNIT
using NUnit.Framework;
#endif

namespace SocialVPN {

  /**
   * SocialNode Class. Extends the RpcIpopNode to support adding friends based
   * on X509 certificates.
   */
  public class SocialNode : ManagedIpopNode {

    /**
     * The current version of SocialVPN.
     */
    public const string VERSION = "SVPN_0.3.0";

    /**
     * The suffix for the DNS names.
     */
    public const string DNSSUFFIX = ".ipop";

    /**
     * The local certificate file name.
     */
    public const string CERTFILENAME = "local.cert";

    /**
     * The DHT TTL.
     */
    public const int DHTTTL = 600;

    /**
     * Dictionary of friends indexed by alias.
     */
    protected readonly Dictionary<string, SocialUser> _friends;

    /**
     * The mapping of aliases to friends.
     */
    protected readonly Dictionary<string, string> _aliases;

    /**
     * The mapping of address to dht keys.
     */
    protected readonly Dictionary<string, string> _addr_to_key;

    /**
     * The certificate directory path.
     */
    protected readonly string _cert_dir;

    /**
     * The local user.
     */
    protected readonly SocialUser _local_user;

    /**
     * The local user certificate.
     */
    protected readonly Certificate _local_cert;

    /**
     * The base64 string representation of local certificate.
     */
    protected readonly string _local_cert_b64;

    /**
     * The identity provider and the social network.
     */
    protected readonly SocialNetworkProvider _snp;

    /**
     * The connection manager.
     */
    protected readonly SocialConnectionManager _scm;

    /**
     * The Rpc handler for socialvpn RPC functions.
     */
    protected readonly SocialRpcHandler _srh;

    /**
     * The main blocking queue used for message passing between threads.
     */
    protected readonly BlockingQueue _queue;

    /**
     * Constructor.
     * @param brunetConfig configuration file for Brunet P2P library.
     * @param ipopConfig configuration file for IP over P2P app.
     */
    public SocialNode(NodeConfig brunetConfig, IpopConfig ipopConfig, 
                      string certDir, string port) : 
                      base(brunetConfig, ipopConfig) {
      _friends = new Dictionary<string, SocialUser>();
      _aliases = new Dictionary<string, string>();
      _addr_to_key = new Dictionary<string, string>();
      _cert_dir = certDir;
      string cert_path = Path.Combine(certDir, CERTFILENAME);
      _local_cert = new Certificate(SocialUtils.ReadFileBytes(cert_path));
      _local_user = new SocialUser(_local_cert);
      _local_cert_b64 = Convert.ToBase64String(_local_cert.X509.RawData);
      _bso.CertificateHandler.AddCACertificate(_local_cert.X509);
      _bso.CertificateHandler.AddSignedCertificate(_local_cert.X509);
      _snp = new SocialNetworkProvider(this.Dht, _local_user, 
                                       _local_cert.X509.RawData, certDir);
      _queue = new BlockingQueue();
      _srh = new SocialRpcHandler(_node, _local_user, _friends, _queue);
      _scm = new SocialConnectionManager(this, _snp, _srh, port, _queue);
      _node.ConnectionTable.ConnectionEvent += ConnectHandler;
      _node.HeartBeatEvent += _scm.HeartBeatHandler;
      _local_user.IP = _marad.LocalIP;
      _local_user.Alias = CreateAlias(_local_user.Uid, _local_user.PCID,
                                     _local_user.DhtKey);
      _marad.MapLocalDNS(_local_user.Alias);
    }

    /**
     * Create a unique alias for a user resource.
     * @param uid the user unique identifier (email).
     * @param pcid the pc identifier.
     * @param dhtKey the friend's dht key.
     * @return a unique user alias used for DNS naming.
     */
    protected virtual string CreateAlias(string uid, string pcid, 
                                         string dhtKey) {
      uid = uid.Replace('@', '.');
      string alias = (pcid + "." + uid + DNSSUFFIX).ToLower();

      // If alias already exists, remove old friend with alias
      if(_aliases.ContainsKey(alias)) {
        string dht_key = _aliases[alias];
        DeleteFriend(_friends[dht_key]);
      }
      else {
        _aliases[alias] = dhtKey;
      }
      return alias;
    }

    /**
     * The connect handler keeps track of when a friend address is added
     * to the connection table.
     * @param obj the connection object containing address of new connection.
     * @param eargs the event arguments.
     */
    public void ConnectHandler(Object obj, EventArgs eargs) {
      Connection new_conn = ((ConnectionEventArgs)eargs).Connection;
      string address = new_conn.Address.ToString();
      if(_addr_to_key.ContainsKey(address)) {
        ProtocolLog.WriteIf(SocialLog.SVPNLog, 
                            String.Format("CONNECT HANDLER: {0} {1} {2}",
                            DateTime.Now.TimeOfDay, _addr_to_key[address],
                            address));
      }
    }

    /**
     * Add local certificate to the DHT.
     */
    public void PublishCertificate() {
      byte[] key_bytes = Encoding.UTF8.GetBytes(_local_user.DhtKey);
      MemBlock keyb = MemBlock.Reference(key_bytes);
      MemBlock value = MemBlock.Reference(_local_cert.X509.RawData);

      Channel q = new Channel();
      q.CloseAfterEnqueue();
      q.CloseEvent += delegate(Object o, EventArgs eargs) {
        try {
          bool success = (bool) (q.Dequeue());
          if(success) {
            ProtocolLog.WriteIf(SocialLog.SVPNLog,
                                String.Format("PUBLISH CERT SUCCESS: {0} {1}",
                                DateTime.Now.TimeOfDay, _local_user.DhtKey));
          }
        } catch (Exception e) {
          ProtocolLog.WriteIf(SocialLog.SVPNLog,e.Message);
          ProtocolLog.WriteIf(SocialLog.SVPNLog,
                              String.Format("PUBLISH CERT FAILURE: {0} {1}", 
                              DateTime.Now.TimeOfDay, _local_user.DhtKey));
        }
      };
      this.Dht.AsyncPut(keyb, value, DHTTTL, q);
    }

    /**
     * Add a friend to socialvpn from an X509 certificate.
     * @param certData the X509 certificate as a byte array.
     */
    public void AddCertificate(byte[] certData) {
      Certificate cert = new Certificate(certData);
      SocialUser friend = new SocialUser(cert);

      // Verification on the certificate by email and fingerprint
      if(friend.DhtKey == _local_user.DhtKey || 
         _friends.ContainsKey(friend.DhtKey)) {
        ProtocolLog.WriteIf(SocialLog.SVPNLog, 
                            String.Format("ADD CERT KEY FOUND: {0} {1}",
                            DateTime.Now.TimeOfDay, friend.DhtKey));
      }
      else if(_snp.ValidateCertificate(certData)) {
        friend.Alias = CreateAlias(friend.Uid, friend.PCID, friend.DhtKey);
        SocialUtils.SaveCertificate(cert, _cert_dir);
        _bso.CertificateHandler.AddCACertificate(cert.X509);
        _friends.Add(friend.DhtKey, friend);
        _addr_to_key.Add(friend.Address, friend.DhtKey);
        AddFriend(friend);
        _srh.PingFriend(friend);

        ProtocolLog.WriteIf(SocialLog.SVPNLog,
                            String.Format("ADD CERT KEY SUCCESS: {0} {1} {2}",
                            DateTime.Now.TimeOfDay, friend.DhtKey,
                            friend.Address));
      }
      else {
        ProtocolLog.WriteIf(SocialLog.SVPNLog, 
                            String.Format("ADD CERT KEY INVALID: {0} {1} {2}",
                            DateTime.Now.TimeOfDay, friend.DhtKey,
                            friend.Address));
      }
    }

    /**
     * Add friend by retreiving certificate from DHT.
     * @param key the DHT key for friend's certificate.
     */
    public void AddDhtFriend(string key) {
      if(key != _local_user.DhtKey && !_friends.ContainsKey(key) &&
         key.Length >= 45) {
        ProtocolLog.WriteIf(SocialLog.SVPNLog, 
                            String.Format("ADD DHT FETCH: {0} {1}", 
                            DateTime.Now.TimeOfDay, key));
        Channel q = new Channel();
        q.CloseAfterEnqueue();
        q.CloseEvent += delegate(Object o, EventArgs eargs) {
          try {
            Hashtable result = (Hashtable) q.Dequeue();
            byte[] certData = (byte[]) result["value"];
            ProtocolLog.WriteIf(SocialLog.SVPNLog, 
                                String.Format("ADD DHT SUCCESS: {0} {1}",
                                DateTime.Now.TimeOfDay, key));
            _queue.Enqueue(new QueueItem(
              QueueItem.Actions.AddCert, certData));
          } catch (Exception e) {
            ProtocolLog.WriteIf(SocialLog.SVPNLog,e.Message);
            ProtocolLog.WriteIf(SocialLog.SVPNLog,
                                String.Format("ADD DHT FAILURE: {0} {1}", 
                                DateTime.Now.TimeOfDay, key));
          }
        };
        byte[] key_bytes = Encoding.UTF8.GetBytes(key);
        MemBlock keyb = MemBlock.Reference(key_bytes);
        this.Dht.AsyncGet(keyb, q);
      }
    }

    /*
     * Add a friend from socialvpn.
     * @param fpr the friend's fingerprint to be added.
     */
    public void AddFriend(string fpr) {
      if(_friends.ContainsKey(fpr)) {
        AddFriend(_friends[fpr]);
      }
    }

    /*
     * Removes a friend from socialvpn.
     * @param fpr the friend's fingerprint to be removed.
     */
    public void RemoveFriend(string fpr) {
      if(_friends.ContainsKey(fpr)) {
        RemoveFriend(_friends[fpr]);
      }
    }


    /*
     * Add a friend from socialvpn.
     * @param friend the friend to be added.
     */
    public void AddFriend(SocialUser friend) {
      Address addr = AddressParser.Parse(friend.Address);
      friend.IP = _marad.RegisterMapping(friend.Alias, addr);
      _node.ManagedCO.AddAddress(addr);
      friend.Access = SocialUser.AccessTypes.Allow.ToString();
    }

    /**
     * Removes (block access) a friend from socialvpn.
     * @param friend the friend to be removed.
     */
    public void RemoveFriend(SocialUser friend) {
      Address addr = AddressParser.Parse(friend.Address);
      _node.ManagedCO.RemoveAddress(addr);
      _marad.UnregisterMapping(friend.Alias);
      friend.Access = SocialUser.AccessTypes.Block.ToString();
    }

    /**
     * Delete (erase) a friend from socialvpn.
     * @param friend the friend to be deleted.
     */
    public void DeleteFriend(SocialUser friend) {
      RemoveFriend(friend);
      SocialUtils.DeleteCertificate(friend.Address, _cert_dir);
      _friends.Remove(friend.DhtKey);
    }

    /**
     * Generates an XML string representing state of the system.
     * @return a string represential the state.
     */
    public string GetState() {
      SocialState state = new SocialState();
      state.Certificate = _local_cert_b64;
      state.LocalUser = _local_user;
      state.Friends = new SocialUser[_friends.Count];
      _friends.Values.CopyTo(state.Friends, 0);
      return SocialUtils.ObjectToXml<SocialState>(state);
    }

    /**
     * The main function, starting point for the program.
     */
    public static new void Main(string[] args) {

      if(args.Length < 3) {
        Console.WriteLine("usage: SocialVPN.exe <brunet.config path> " + 
                           "<ipop.config path> <http port> [email] [pcid] " + 
                           "[\"name\"]");
        return;
      }

      NodeConfig node_config = Utils.ReadConfig<NodeConfig>(args[0]);
      IpopConfig ipop_config = Utils.ReadConfig<IpopConfig>(args[1]);

      if(!System.IO.Directory.Exists(node_config.Security.CertificatePath)) {
        string name, uid, pcid, version, country;
        if(args.Length >= 6) {
          uid = args[3];
          pcid = args[4];
          name = args[5];
        }
        else {
          Console.Write("Enter Name (First Last): ");
          name = Console.ReadLine();
          Console.Write("Enter Email Address: ");
          uid = Console.ReadLine();
          Console.Write("Enter a unique name for this machine: ");
          pcid = Console.ReadLine();
        }

        if(uid.Length < 1 || pcid.Length < 1 || name.Length < 1) {
          return;
        }

        country = "Undefined";
        version = VERSION;
        node_config.NodeAddress = Utils.GenerateAHAddress().ToString();
        Utils.WriteConfig(args[0], node_config);
        SocialUtils.CreateCertificate(uid, name, pcid, version, country,
                                      node_config.NodeAddress, 
                                      node_config.Security.CertificatePath,
                                      node_config.Security.KeyPath);
      }

      SocialNode node = new SocialNode(node_config, ipop_config, 
                                       node_config.Security.CertificatePath,
                                       args[2]);
      node.Run();
    }
  }
}
