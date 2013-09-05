﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Collections.Concurrent;
using System.Threading;

using Criteo.Memcache.Headers;
using Criteo.Memcache.Requests;
using Criteo.Memcache.Configuration;
using Criteo.Memcache.Transport;

namespace Criteo.Memcache.Node
{
    internal class MemcacheNode : IMemcacheNode
    {
        private readonly BlockingCollection<IMemcacheTransport> _transportPool;
        private int _workingTransport;
        private volatile bool _isAlive = false;
        private volatile CancellationTokenSource _tokenSource;
        private MemcacheClientConfiguration _configuration;

        private static SynchronousTransportAllocator DefaultAllocator = 
            (endPoint, authenticator, queueTimeout, pendingLimit, setupAction, autoConnect)
                => new MemcacheSocket(endPoint, authenticator, queueTimeout, pendingLimit, setupAction, autoConnect);

        #region Events
        private void OnTransportError(Exception e)
        {
            if (TransportError != null)
                TransportError(e);
        }
        public event Action<Exception> TransportError;

        private void OnMemcacheError(MemcacheResponseHeader h, IMemcacheRequest e)
        {
            if (MemcacheError != null)
                MemcacheError(h, e);
        }
        public event Action<MemcacheResponseHeader, IMemcacheRequest> MemcacheError;

        private void OnMemcacheResponse(MemcacheResponseHeader h, IMemcacheRequest e)
        {
            if (MemcacheResponse != null)
                MemcacheResponse(h, e);
        }
        public event Action<MemcacheResponseHeader, IMemcacheRequest> MemcacheResponse;

        public event Action<IMemcacheNode> NodeDead;

        public event Action<IMemcacheNode> NodeAlive;

        private void RegisterEvents(IMemcacheTransport transport)
        {
            transport.MemcacheError += OnMemcacheError;
            transport.MemcacheResponse += OnMemcacheResponse;
            transport.TransportError += OnTransportError;
            transport.TransportDead += OnTransportDead;
        }

        private void OnTransportDead(IMemcacheTransport transort)
        {
            lock (this)
            {
                if (0 == Interlocked.Decrement(ref _workingTransport))
                {
                    _isAlive = false;
                    if (NodeDead != null)
                        NodeDead(this);
                }
            }
        }
        #endregion Events

        private readonly EndPoint _endPoint;
        public EndPoint EndPoint
        {
            get { return _endPoint; }
        }   

        public MemcacheNode(EndPoint endPoint, MemcacheClientConfiguration configuration)
        {
            _configuration = configuration;
            _endPoint = endPoint;
            _transportPool = new BlockingCollection<IMemcacheTransport>();

            for (int i = 0; i < configuration.PoolSize; ++i)
            {
                var transport = (configuration.SynchronousTransportFactory ?? DefaultAllocator)
                                    (endPoint, 
                                    configuration.Authenticator, 
                                    configuration.TransportQueueTimeout, 
                                    configuration.TransportQueueLength,
                                    TransportAvailable,
                                    false);
                TransportAvailable(transport);
            }
        }


        private void TransportAvailable(IMemcacheTransport transport)
        {
            if (_tokenSource == null || _tokenSource.IsCancellationRequested)
                lock (this)
                    if (_tokenSource == null || _tokenSource.IsCancellationRequested)
                        _tokenSource = new CancellationTokenSource();

            if (!transport.Registered)
            {
                RegisterEvents(transport);

                Interlocked.Increment(ref _workingTransport);
                if (!_isAlive)
                    lock (this)
                        if (!_isAlive)
                        {
                            _isAlive = true;
                            if (NodeAlive != null)
                                NodeAlive(this);
                        }
            }

            _transportPool.Add(transport);
        }

        public bool IsDead
        {
            get { return !_isAlive; }
        }

        public bool TrySend(IMemcacheRequest request, int timeout)
        {
            IMemcacheTransport transport;
            try
            {
                while (_tokenSource != null && !_tokenSource.IsCancellationRequested
                    && _transportPool.TryTake(out transport, timeout, _tokenSource.Token))
                {
                    bool sent = false;
                    try
                    {
                        sent = transport.TrySend(request);

                        if (sent)
                            return true;
                    }
                    catch (Exception)
                    {
                        // if anything happen, don't let a transport outside of the pool
                        _transportPool.Add(transport);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // someone called for a cancel on the pool.TryTake and already raised the problem
                return false;
            }

            return false;
        }

        public void Dispose()
        {
            IMemcacheTransport transport;
            while(_transportPool.TryTake(out transport))
                transport.Dispose();
        }

        // for testing purpose only !!!
        internal int PoolSize { get { return _transportPool.Count; } }
    }
}
