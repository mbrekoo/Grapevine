﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Grapevine
{
    public abstract class HttpResponder : IDisposable
    {
        #region Instance Variables

        protected volatile bool _listening;

        protected string _webroot = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location) + @"\webroot";
        protected string _default = "index.html";
        protected string _host    = "localhost";
        protected string _port    = "1234";
        protected int _maxt       = 5;

        protected readonly Thread _listenerThread;
        protected readonly Thread[] _workers;
        protected readonly List<MethodInfo> _methods;

        protected readonly HttpListener _listener = new HttpListener();
        protected readonly ManualResetEvent _stop = new ManualResetEvent(false);
        protected readonly ManualResetEvent _ready = new ManualResetEvent(false);
        protected Queue<HttpListenerContext> _queue = new Queue<HttpListenerContext>();

        #endregion

        #region Constructors

        public HttpResponder()
        {
            _workers = new Thread[_maxt];
            _listenerThread = new Thread(HandleRequests);
            _methods = this.GetType().GetMethods().Where(mi => mi.GetCustomAttributes(true).Any(attr => attr is Responder)).ToList<MethodInfo>();
        }

        public HttpResponder(string host, string port, int maxThreads)
        {
            _host = host;
            _port = port;
            _maxt = maxThreads;

            _workers = new Thread[_maxt];
            _listenerThread = new Thread(HandleRequests);
            _methods = this.GetType().GetMethods().Where(mi => mi.GetCustomAttributes(true).Any(attr => attr is Responder)).ToList<MethodInfo>();
        }

        #endregion

        #region Public Methods

        public void Start()
        {
            _listening = true;
            _listener.Prefixes.Add(String.Format(@"http://{0}:{1}/", _host, _port));
            _listener.Start();
            _listenerThread.Start();

            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(Worker);
                _workers[i].Start();
            }
        }

        public void Stop()
        {
            _stop.Set();
            foreach (Thread worker in _workers)
                worker.Join();
            _listener.Stop();
            _listening = false;
        }

        public void Dispose()
        {
            Stop();
        }

        #endregion

        #region Public Properties

        public bool IsListening
        {
            get
            {
                return _listening;
            }
        }

        public string WebRoot
        {
            get
            {
                return _webroot;
            }
            set
            {
                if (VerifyWebRoot(value))
                {
                    _webroot = value;
                }
            }
        }

        public string Host
        {
            get
            {
                return _host;
            }
            set
            {
                if (!_listening)
                {
                    _host = value;
                }
                else
                {
                    throw new InvalidStateException();
                }
            }
        }

        public string Port
        {
            get
            {
                return _port;
            }
            set
            {
                if (!_listening)
                {
                    _port = value;
                }
                else
                {
                    throw new InvalidStateException();
                }
            }
        }

        public int MaxThreads
        {
            get
            {
                return _maxt;
            }
            set
            {
                if (!_listening)
                {
                    _maxt = value;
                }
                else
                {
                    throw new InvalidStateException();
                }
            }
        }

        #endregion

        #region Threading, Queuing and Processing

        private void HandleRequests()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    QueueContext(context);
                }
                catch { }
            }
        }

        private void QueueContext(HttpListenerContext context)
        {
            try
            {
                lock (_queue)
                {
                    _queue.Enqueue(context);
                    _ready.Set();
                }
            }
            catch
            {
                return;
            }
        }

        private void Worker()
        {
            WaitHandle[] wait = new[] { _ready, _stop };
            while (0 == WaitHandle.WaitAny(wait))
            {
                HttpListenerContext context;
                lock (_queue)
                {
                    if (_queue.Count > 0)
                        context = _queue.Dequeue();
                    else
                    {
                        _ready.Reset();
                        continue;
                    }
                }

                try
                {
                    ProcessRequest(context);
                }
                catch { return; }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var x = _methods.ToList<MethodInfo>().Where(mi => mi != null).First();
                var method = (from mi in _methods where mi.GetCustomAttributes(true).Any(attr => context.Request.RawUrl.Matches(((Responder)attr).PathInfo) && ((Responder)attr).Method.ToString().Equals(context.Request.HttpMethod.ToUpper())) select mi).First();
                method.Invoke(this, new object[] { context });
            }
            catch
            {
                if (VerifyWebRoot(_webroot))
                {
                    var filename = GetFilePath(context.Request.RawUrl);
                    if (filename != null)
                    {
                        SendFileResponse(context, filename);
                        return;
                    }
                }

                NotImplemented(context);
            }
        }

        #endregion

        #region Responding Helpers

        private bool VerifyWebRoot(string webroot)
        {
            try
            {
                if (!Directory.Exists(webroot))
                {
                    Directory.CreateDirectory(webroot);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void NotImplemented(HttpListenerContext context)
        {
            var payload = "<h1>Not Found</h1>";
            var buffer = Encoding.UTF8.GetBytes(payload);
            var length = buffer.Length;

            context.Response.ContentType = "text/html";
            context.Response.StatusCode = 404;
            context.Response.StatusDescription = "Not Found";
            context.Response.ContentLength64 = length;
            context.Response.OutputStream.Write(buffer, 0, length);
            context.Response.OutputStream.Close();
            context.Response.Close();
        }

        private string GetFilePath(string rawurl)
        {
            var filename = ((rawurl.IndexOf("?") > -1) ? rawurl.Split('?') : rawurl.Split('#'))[0].Replace('/', '\\').Substring(1);
            var path = Path.Combine(_webroot, filename);

            if (Directory.Exists(path))
            {
                path = Path.Combine(path, _default);
            }

            if (File.Exists(path))
            {
                return path;
            }

            return null;
        }

        protected void SendResponse(HttpListenerContext context, string payload)
        {
            var buffer = Encoding.UTF8.GetBytes(payload);
            var length = buffer.Length;

            context.Response.StatusDescription = "Success";
            context.Response.ContentType = "text/plain";
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = length;
            context.Response.OutputStream.Write(buffer, 0, length);
            context.Response.OutputStream.Close();
            context.Response.Close();
        }

        protected void SendTextResponse(HttpListenerContext context, string payload)
        {
            var buffer = Encoding.UTF8.GetBytes(payload);
            var length = buffer.Length;

            context.Response.ContentLength64 = length;
            context.Response.OutputStream.Write(buffer, 0, length);
            context.Response.OutputStream.Close();
            context.Response.Close();
        }

        protected void SendFileResponse(HttpListenerContext context, string path)
        {
            var type   = ContentTypes.GetContentType(path);
            var buffer = GetFileBytes(path, type.IsText);
            var length = buffer.Length;

            context.Response.ContentType = type.MIMEType;
            context.Response.ContentLength64 = length;
            context.Response.OutputStream.Write(buffer, 0, length);
            context.Response.OutputStream.Close();
            context.Response.Close();
        }

        private byte[] GetFileBytes(string path, bool istext)
        {
            byte[] buffer;

            if (istext)
            {
                var reader = new StreamReader(path);
                buffer = Encoding.UTF8.GetBytes(reader.ReadToEnd());
                reader.Close();
            }
            else
            {
                var finfo = new FileInfo(path);
                var reader = new BinaryReader(File.Open(path, FileMode.Open, FileAccess.Read));
                buffer = reader.ReadBytes((int)finfo.Length);
                reader.Close();
            }

            return buffer;
        }

        #endregion
    }
}
