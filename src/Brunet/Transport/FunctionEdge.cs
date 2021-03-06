/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2005  University of California
Copyright (C) 2007 P. Oscar Boykin <boykin@pobox.com> University of Florida

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
using System.Threading;
using System.Collections;

namespace Brunet.Transport
{

  /**
  * A Edge which does its transport locally
  * by calling a method on the other edge
  *
  * This Edge is for debugging purposes on
  * a single machine in a single process.
  */

  public class FunctionEdge : Edge
  {

    protected readonly int _l_id;
    protected readonly int _r_id;

    public FunctionEdge(IEdgeSendHandler s, int local_id, int remote_id, bool is_in) : base(s, is_in)
    {
      _l_id = local_id;
      _r_id = remote_id;
    }

    protected FunctionEdge _partner;
    public FunctionEdge Partner
    {
      get
      {
        return _partner;
      }
      set
      {
        Interlocked.Exchange<FunctionEdge>(ref _partner, value);
      }
    }


    public override TransportAddress.TAType TAType
    {
      get
      {
        return TransportAddress.TAType.Function;
      }
    }

    public int ListenerId {
      get { return _l_id; }
    }

    protected TransportAddress _local_ta;
    public override TransportAddress LocalTA
    {
      get {
        if ( _local_ta == null ) {
          _local_ta = TransportAddressFactory.CreateInstance("brunet.function://localhost:"
                                    + _l_id.ToString());
        }
        return _local_ta;
      }
    }
    protected TransportAddress _remote_ta;
    public override TransportAddress RemoteTA
    {
      get {
        if ( _remote_ta == null ) {
          _remote_ta = TransportAddressFactory.CreateInstance("brunet.function://localhost:"
                                    + _r_id.ToString());
        }
        return _remote_ta;
      }
    }
  }
}
