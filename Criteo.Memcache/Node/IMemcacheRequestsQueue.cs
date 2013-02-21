﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Criteo.Memcache.Requests;

namespace Criteo.Memcache.Node
{
    public interface IMemcacheRequestsQueue
    {
        /// <summary>
        /// Blocking method that returns a request from the node queue
        /// </summary>
        /// <returns>Request sent to the underlying node or null if the node is requested to stop</returns>
        IMemcacheRequest Take();

        /// <summary>
        /// Blocking method that returns a request from the node queue
        /// </summary>
        /// <param name="request">The first request in the queue</param>
        /// <param name="timeout" />
        /// <returns>false if the queue was empty after the timeout</returns>
        bool TryTake(out IMemcacheRequest request, int timeout);
    }
}
