﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace Kayak.Http
{
    // dispose called on error condition.
    interface IOutputSegment : IDisposable
    {
        void AttachNextSegment(IOutputSegment c);
        void AttachSocket(ISocket socket);
    }

    abstract class BaseConsumer : IOutputSegment
    {
        protected ISocket socket;
        IOutputSegment previous;

        public BaseConsumer(IOutputSegment previous)
        {
            this.previous = previous;
        }

        public virtual void AttachSocket(ISocket socket)
        {
            previous = null;
            this.socket = socket;
        }

        public abstract void AttachNextSegment(IOutputSegment next);

        public virtual void Dispose()
        {
            if (socket != null)
            {
                var s = socket;
                socket = null;
                s.Dispose();
            }

            if (previous != null)
            {
                var p = previous;
                previous = null;
                p.Dispose();
            }
        }
    }

    class LastConsumer : BaseConsumer
    {
        public LastConsumer(IOutputSegment previous) : base(previous) { }

        public override void AttachNextSegment(IOutputSegment c)
        {
            throw new NotSupportedException();
        }

        public override void AttachSocket(ISocket socket)
        {
            base.AttachSocket(socket);

            Shutdown();
        }

        public override void Dispose()
        {
            base.Dispose();
            Shutdown();
        }

        void Shutdown()
        {
            if (socket != null)
            {
                var s = socket;
                socket = null;
                s.End();
                s.Dispose();
            }
        }

    }

    class MessageConsumer : BaseConsumer, IDataConsumer
    {
        IDataProducer message;
        IOutputSegment next;
        IDisposable abortMessage;
        List<byte[]> buffer;
        bool gotEnd;
        Action continuation;

        public MessageConsumer(IOutputSegment previous, IDataProducer message) : base(previous)
        {
            this.message = message;
        }

        public override void AttachNextSegment(IOutputSegment c)
        {
            next = c;
            DoConnect();
            HandOffSocketIfPossible();
        }

        public override void AttachSocket(ISocket socket)
        {
            base.AttachSocket(socket);
            
            DoConnect();

            if (buffer != null)
                DrainBuffer();

            HandOffSocketIfPossible();
        }

        public override void Dispose()
        {
            base.Dispose();
            AbortProducer();
        }

        void DoConnect()
        {
            if (message != null)
            {
                var m = message;
                message = null;
                abortMessage = m.Connect(this);
            }
        }

        void HandOffSocketIfPossible()
        {
            if (next == null || !gotEnd) return;

            if (abortMessage != null)
                abortMessage.Dispose();

            var s = socket;
            socket = null;
            next.AttachSocket(s);
        }

        void AddToBuffer(ArraySegment<byte> data)
        {
            if (buffer == null)
                buffer = new List<byte[]>();

            byte[] newBuffer = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, newBuffer, 0, data.Count);
            buffer.Add(newBuffer);
        }

        void DrainBuffer()
        {
            // XXX probably worthwhile to combine first two elements into a single packet
            foreach (var b in buffer)
                socket.Write(new ArraySegment<byte>(b), null);

            if (continuation != null)
            {
                var c = continuation;
                continuation = null;
                c();
            }
        }

        public bool OnData(ArraySegment<byte> data, Action c)
        {
            if (continuation != null)
                throw new InvalidOperationException("Continuation was pending.");

            // XXX probably worthwhile to always buffer first packet,
            // being as this is an HTTP implementation, and it will usually
            // be headers.
            if (socket == null)
            {
                AddToBuffer(data);

                if (c != null)
                {
                    this.continuation = c;
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
                return socket.Write(data, continuation);
        }

        public void OnError(Exception e)
        {
            Debug.WriteLine("Error during response.");
            e.DebugStacktrace();
            socket.Dispose();
        }

        public void OnEnd()
        {
            gotEnd = true;
            HandOffSocketIfPossible();
        }

        void AbortProducer()
        {
            if (abortMessage != null)
                abortMessage.Dispose();
        }
    }

}
