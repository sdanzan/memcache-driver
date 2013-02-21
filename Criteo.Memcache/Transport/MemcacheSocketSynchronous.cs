﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Criteo.Memcache.Node;
using Criteo.Memcache.Requests;
using Criteo.Memcache.Authenticators;
using Criteo.Memcache.Headers;
using Criteo.Memcache.Exceptions;

namespace Criteo.Memcache.Transport
{
    internal class MemcacheSocketSynchronous : MemcacheSocketBase, ISynchronousTransport
    {
        private Thread _receivingThread;
        private CancellationTokenSource _token;
        private ConcurrentQueue<IMemcacheRequest> _pending = new ConcurrentQueue<IMemcacheRequest>();
        private volatile bool _isAlive;
        private volatile Action<MemcacheSocketSynchronous> _setupAction;

        public MemcacheSocketSynchronous(EndPoint endpoint, IMemcacheAuthenticator authenticator, IMemcacheNode node, int queueTimeout, int pendingLimit, Action<MemcacheSocketSynchronous> setupAction)
            : base(endpoint, authenticator, queueTimeout, pendingLimit, null, node)
        {
            _setupAction = setupAction;
        }

        private void StartReceivingThread()
        {
            var buffer = new byte[MemcacheResponseHeader.SIZE];
            _receivingThread = new Thread(t =>
            {
                var token = (CancellationToken)t;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int received = 0;
                        do
                        {
                            received += Socket.Receive(buffer, received, MemcacheResponseHeader.SIZE - received, SocketFlags.None);
                        } while (received < MemcacheResponseHeader.SIZE);

                        var header = new MemcacheResponseHeader(buffer);

                        // in case we have a message ! (should not happen for a set)
                        byte[] extra = null;
                        if (header.ExtraLength > 0)
                        {
                            extra = new byte[header.ExtraLength];
                            received = 0;
                            do
                            {
                                received += Socket.Receive(extra, received, header.ExtraLength - received, SocketFlags.None);
                            } while (received < header.ExtraLength);
                        }
                        byte[] message = null;
                        int messageLength = (int)(header.TotalBodyLength - header.ExtraLength);
                        if (messageLength > 0)
                        {
                            message = new byte[messageLength];
                            received = 0;
                            do
                            {
                                received += Socket.Receive(message, received, messageLength - received, SocketFlags.None);
                            } while (received < messageLength);
                        }

                        // should assert we have the good request
                        var request = UnstackToMatch(header);

                        if (_memcacheResponse != null)
                            _memcacheResponse(header, request);
                        if (header.Status != Status.NoError && _memcacheError != null)
                            _memcacheError(header, request);

                        if (request != null)
                            request.HandleResponse(header, extra, message);
                    }
                    catch (Exception e)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            if (_transportError != null)
                                _transportError(e);

                            Reset();
                        }
                    }
                }
            });
            _receivingThread.Start(_token.Token);
        }

        protected override void Start()
        {
            _token = new CancellationTokenSource();
            StartReceivingThread();
            Authenticate();

            lock (this)
            {
                if (_setupAction != null)
                    _setupAction(this);
                _setupAction = null;
                _isAlive = true;
            }
        }

        protected override void ShutDown()
        {
            lock (this)
                _isAlive = false;

            if (_token != null)
                _token.Cancel();
            if (Socket != null)
            {
                Socket.Dispose();
                Socket = null;
            }
        }

        private bool Authenticate()
        {
            bool authDone = false;
            IMemcacheRequest request = null;
            Status authStatus = Status.NoError;
            if (AuthenticationToken != null)
            {
                while (!authDone)
                {
                    authStatus = AuthenticationToken.StepAuthenticate(out request);

                    switch (authStatus)
                    {
                        // auth OK, clear the token
                        case Status.NoError:
                            AuthenticationToken = null;
                            authDone = true;
                            break;
                        case Status.StepRequired:
                            if (request == null)
                            {
                                if (_transportError != null)
                                    _transportError(new AuthenticationException("Unable to authenticate : step required but no request from token"));
                                Reset();
                                return false;
                            }
                            SendRequest(request);
                            break;
                        default:
                            if (_transportError != null)
                                _transportError(new AuthenticationException("Unable to authenticate : status " + authStatus.ToString()));
                            Reset();
                            return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Synchronously sends the request
        /// </summary>
        /// <param name="request"></param>
        public bool TrySend(IMemcacheRequest request)
        {
            try
            {
                if (request == null || !_isAlive)
                    return false;

                SendRequest(request);
            }
            catch (Exception e)
            {
                if (_transportError != null)
                    _transportError(e);

                Reset();

                return false;
            }

            return true;
        }

        private void SendRequest(IMemcacheRequest request)
        {
            var buffer = request.GetQueryBuffer();

            EnqueueRequest(request);
            int sent = 0;
            do
            {
                sent += Socket.Send(buffer, sent, buffer.Length - sent, SocketFlags.None);
            } while (sent != buffer.Length);
        }

        public void SetupAction(Action<ISynchronousTransport> action)
        {
            lock (this)
            {
                if (_isAlive)
                    // the transport is already up => execute now
                    action(this);
                else
                    // plan to execute when the transport will be alive
                    _setupAction = action;
            }
        }
    }
}
