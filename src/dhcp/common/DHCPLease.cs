using System;
using System.IO;
using System.Collections;

namespace Ipop {
  public struct Lease {
    public byte [] ip;
    public byte [] hwaddr;
    public DateTime expiration;
  }

  public class DHCPLeaseResponse {
    public byte [] ip;
    public byte [] netmask;
    public byte [] leasetime;
  }

  class DHCPLease {
    int index, size, leasetime;
    long logsize;
    string namespace_value;
    byte [] netmask;
    byte [] lower;
    byte [] upper;
    byte [] leasetimeb;
    byte [][] reservedIP;
    byte [][] reservedMask;
    ArrayList LeaseIPs;
    ArrayList LeaseHWAddrs;
    ArrayList LeaseExpirations;
    object LeaseLock;

    public DHCPLease(IPOPNamespace config) {
      leasetime = config.leasetime;
      leasetimeb = new byte[]{((byte) ((leasetime >> 24))),
        ((byte) ((leasetime >> 16))),
        ((byte) ((leasetime >> 8))),
        ((byte) (leasetime))};
      namespace_value = config.value;
      logsize = config.LogSize * 1024; /* Bytes */
      lower = DHCPCommon.StringToBytes(config.pool.lower, '.');
      upper = DHCPCommon.StringToBytes(config.pool.upper, '.');
      netmask = DHCPCommon.StringToBytes(config.netmask, '.');

      if(config.reserved != null) {
        reservedIP = new byte[config.reserved.value.Length + 1][];
        reservedMask = new byte[config.reserved.value.Length + 1][];
        for(int i = 1; i < config.reserved.value.Length + 1; i++) {
          reservedIP[i] = DHCPCommon.StringToBytes(
            config.reserved.value[i-1].ip, '.');
          reservedMask[i] = DHCPCommon.StringToBytes(
            config.reserved.value[i-1].mask, '.');
        }
      }
      else {
        reservedIP = new byte[1][];
        reservedMask = new byte[1][];
      }
      reservedIP[0] = new byte[4];

      for(int i = 0; i < 3; i++)
        reservedIP[0][i] = (byte) (lower[i] & netmask[i]);
      reservedIP[0][3] = 1;
      reservedMask[0] = new byte[4] {255, 255, 255, 255};

      this.index = 0;
      this.size = 0;
      LeaseIPs = new ArrayList();
      LeaseHWAddrs = new ArrayList();
      LeaseExpirations = new ArrayList();
      LeaseLock = new object();
      if(!this.ReadLog()) {
        System.Console.WriteLine("Error can't read log files!\nShutting down...");
      }
    }

    public DHCPLeaseResponse GetLease(byte [] hwaddr) {
      bool success = true;
      byte []ip = new byte[4] {0,0,0,0};
      lock(LeaseLock)
      {
        int index = CheckForPreviousLease(hwaddr);
        if(index == -1)
          index = GetNextAvailableIP(hwaddr);
        if(index >= 0) {
          ip = (byte []) LeaseIPs[index];
          LeaseHWAddrs[index] = hwaddr;
          LeaseExpirations[index] = DateTime.Now.AddSeconds(leasetime);
          success = UpdateLog(index);
        }
      }
      DHCPLeaseResponse leaseReturn;
      if(success)
      {
        leaseReturn = new DHCPLeaseResponse();
        leaseReturn.ip = ip;
        leaseReturn.netmask = netmask;
        leaseReturn.leasetime = leasetimeb;
      }
      else
      {
        /* This effectively nullifies any dhcp requests that occur when */
        /* there are some faults occuring */
        index--;
        LeaseExpirations[index] = 0;
        leaseReturn = null;
      }
      return leaseReturn;
    }

    public int CheckForPreviousLease(byte [] hwaddr) {
      for (int i = 0; i < LeaseHWAddrs.Count; i++) {
        for (int j = 0; j < hwaddr.Length; j++) {
          if (hwaddr[j] != ((byte []) LeaseHWAddrs[i])[j])
            break;
          else if(j == hwaddr.Length - 1)
            return i;
        }
      }
      return -1;
    }

/*  We no longer acknowledge requests for specific IPs
    public int CheckRequestedIP(byte [] ip) {
      if(!ValidIP(ip))
        return -1;
      int start = 0, end = leaselist.Count;
      int index = leaselist.Count / 2, ip_check;
      int ip_key = keygen(ip), count = 0, term = (int)
        Math.Ceiling(Math.Log((double) end));

      if(leaselist.Count == 0)
        return -1;

      while(count != term) {
        ip_check = keygen(((Lease)leaselist.GetByIndex(index)).ip);
        if(ip_key == ip_check)
          return index;
        else if(ip_key > ip_check) {
          start = index;
          index = (index + end) / 2;
        }
        else {
          end = index;
          index = (start + index) / 2;
        }
        count++;
      }
      return -1;
    }*/

    public int GetNextAvailableIP(byte [] hwaddr) {
      int temp = this.index, count = LeaseIPs.Count;
      DateTime now = DateTime.Now;
      byte [] ip = null;

      if(this.size == 0) {
        if(count == 0) {
          ip = lower;
          if(!ValidIP(ip))
            ip = IncrementIP(lower);
        }
        else {
          ip = IncrementIP((byte []) ((byte []) 
            LeaseIPs[this.index-1]).Clone());
        }
        LeaseIPs.Add(ip);
        LeaseHWAddrs.Add(hwaddr.Clone());
        LeaseExpirations.Add(now.AddDays(leasetime));
        return this.index++;
      }
      else {
        /* Find the first expired lease and return it */
        do {
          if(this.index >= this.size)
            this.index = 0;
          if(((DateTime) LeaseExpirations[index]) < now)
            return this.index;
          this.index++;
        } while(this.index != temp);
      }
      return -1;
    }

    public bool ValidIP(byte [] ip) {
      /* No 255 or 0 in ip[3]] */
      if(ip[3] == 255 || ip[3] == 0)
        return false;
      /* Check range */
      for(int i = 0; i < ip.Length; i++)
        if(ip[i] < lower[i] || ip[i] > upper[i])
          return false;
      /* Check Reserved */
      for(int i = 0; i < reservedIP.Length; i++) {
        for(int j = 0; j < reservedIP[i].Length; j++) {
          if((ip[j] & reservedMask[i][j]) != 
            (reservedIP[i][j] & reservedMask[i][j]))
            break;
          if(j == reservedIP[i].Length - 1)
            return false;
        }
      }
      return true;
    }

    public int keygen(byte [] input) {
      int key = 0;
      for(int i = 0; i < input.Length; i++)
        key += input[i] << 8*i;
      return key;
    }

    public byte [] IncrementIP(byte [] ip) {
      if(ip[3] == 0) {
        ip[3] = 1;
      }
      else if(ip[3] == 254 || ip[3] == upper[3]) {
        ip[3] = lower[3];
        if(ip[2] < upper[2])
          ip[2]++;
        else {
          ip[2] = lower[2];
          if(ip[1] < upper[1])
            ip[1]++;
          else {
            ip[1] = lower[1];
            if(ip[0] < upper[0])
              ip[0]++;
            else {
              ip[0] = lower[0];
              this.size = this.index;
              this.index = 0;
            }
          }
        }
      }
      else {
        ip[3]++;
      }

      if(!ValidIP(ip))
        ip = IncrementIP(ip);

      return ip;
    }

    public bool UpdateLog(int index) {
      bool success = true;
      try {
        FileStream file = new FileStream("logs/" + namespace_value + ".log",
            FileMode.Append, FileAccess.Write);
        StreamWriter sw = new StreamWriter(file);
        sw.WriteLine(index);
        sw.WriteLine(DHCPCommon.BytesToString((byte[]) LeaseIPs[index], '.'));
        sw.WriteLine(DHCPCommon.BytesToString((byte[]) LeaseHWAddrs[index], ':'));
        sw.WriteLine(((DateTime) LeaseExpirations[index]).Ticks);
        sw.Close();
        file.Close();

        file = new FileStream("logs/" + namespace_value + ".log",
            FileMode.OpenOrCreate, FileAccess.Read);
        long length = file.Length;
        file.Close();
        if(length > logsize) {
            success = StoreOldLog();
            if(success)
                success = NewLog();
        }
      }
      catch (Exception)
      {
        success = false;
      }
      return success;
    }

    public bool StoreOldLog() {
      bool success = true;
      try {
        FileStream fileold = new FileStream("logs/" + namespace_value + ".log",
            FileMode.OpenOrCreate, FileAccess.Read);
        FileStream filenew = new FileStream("logs/" + namespace_value + ".log.bak",
            FileMode.OpenOrCreate, FileAccess.Write);
        StreamReader sr = new StreamReader(fileold);
        StreamWriter sw = new StreamWriter(filenew);
        sw.Write(sr.ReadToEnd());
        sr.Close();
        sw.Close();
        fileold.Close();
        filenew.Close();
      }
      catch (Exception)
      {
        success = false;
      }
      return success;
    }

    public bool NewLog() {
      bool success = true;
      try {
        FileStream file = new FileStream("logs/" + namespace_value + ".log",
            FileMode.Create, FileAccess.Write);
        StreamWriter sw = new StreamWriter(file);
        for(int i = 0; i < LeaseIPs.Count; i++) {
            sw.WriteLine(i);
            sw.WriteLine(DHCPCommon.BytesToString((byte[]) LeaseIPs[i], '.'));
            sw.WriteLine(DHCPCommon.BytesToString((byte[]) LeaseHWAddrs[i], ':'));
            sw.WriteLine(((DateTime) LeaseExpirations[i]).Ticks);
        }
        sw.Close();
        file.Close();
      }
      catch (Exception)
      {
        success = false;
      }
      return success;
    }

    public bool ReadLog() {
      bool success = true;
      try {
        FileStream file = new FileStream("logs/" + namespace_value + ".log",
            FileMode.OpenOrCreate, FileAccess.Read);
        StreamReader sr = new StreamReader(file);
        string value = "";
        int index = 0;
        while((value = sr.ReadLine()) != null) {
            index = Int32.Parse(value);
            string ip_str = sr.ReadLine();
            string hw_str = sr.ReadLine();
            Console.WriteLine(ip_str);
            Console.WriteLine(hw_str);
            if(LeaseIPs.Count <= index) {
            LeaseIPs.Add(DHCPCommon.StringToBytes(ip_str, '.'));
            LeaseHWAddrs.Add(DHCPCommon.StringToBytes(hw_str, ':'));
            LeaseExpirations.Add(new DateTime(long.Parse(sr.ReadLine())));
            this.index++;
            }
            else {
            LeaseIPs[index] = DHCPCommon.StringToBytes(ip_str, '.');
            LeaseHWAddrs[index] = DHCPCommon.StringToBytes(hw_str, ':');
            LeaseExpirations[index] = new DateTime(long.Parse(sr.ReadLine()));
            }
        }
        sr.Close();
        file.Close();
      }
      catch (Exception)
      {
        success = false;
      }
      return success;
    }

    public void WriteCache() {
      for(int i = 0; i < LeaseIPs.Count; i++) {
        Console.WriteLine(i);
        Console.WriteLine(DHCPCommon.BytesToString((byte[]) LeaseIPs[i], '.'));
        Console.WriteLine(DHCPCommon.BytesToString((byte[]) LeaseHWAddrs[i], ':'));
        Console.WriteLine(((DateTime) LeaseExpirations[i]).Ticks);
        Console.WriteLine("\n");
      }
    }
  }
}