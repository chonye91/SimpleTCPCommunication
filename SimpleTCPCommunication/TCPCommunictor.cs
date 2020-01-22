using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnitsNet;

namespace SimpleTCPCommunication
{
    #region delegates
    public delegate void OnDataArriveddDel(object data);

    public delegate void OnConnectionChangedDel(bool isConnected);

    #endregion

    /// <summary>
    /// Create with no affort a TCP Connection 
    /// </summary>
    public class TCPCommunictor : IDisposable
    {
        #region Data members
        public event OnConnectionChangedDel OnConnectionChanged;
        public event OnDataArriveddDel OnDataArrived;

        private bool _isConnected;
        private Information _bufferSize;
        private Duration _reconnectInterval;
        private Duration _timeout;
        private NetworkStream _nwStream;
        private bool _isOpen;
        private IPAddress _localAdd;
        private TcpListener _listener;
        private TcpClient _client;
        #endregion

        #region Properties
        /// <summary>
        /// The port in which the data will relay and received
        /// </summary>
        public ushort Port { get; private set; }

        /// <summary>
        /// The Address to send the data
        /// </summary>
        public IPAddress IP { get; private set; }

        /// <summary>
        /// Status of the connection
        /// </summary>
        public bool IsConnected
        {
            get { return _isConnected; }
            set
            {
                if (value == _isConnected)
                    return;

                _isConnected = value;
                OnConnectionChanged?.Invoke(_isConnected);
            }
        }
        #endregion

        #region Initialization

        /// <summary>
        /// Create the TCPCommunicator
        /// </summary>
        /// <param name="IsServer">true if this connection is the Server, false otherwise</param>
        /// <param name="port">the port to connect, expect <see cref="ushort"/> between 0 and 65553</param>
        /// <param name="address">the address as a <see cref="string"/>. this can be either IPv4 or IPv6 address</param>
        /// <param name="buffersize">the size of the buffer to manage</param>
        /// <param name="reconnectInterval">the <see cref="Duration"/> between reconnection attempt</param>
        /// <param name="timeout">the <see cref="Duration"/> to time-out</param>
        public TCPCommunictor(bool IsServer,
                              ushort port,
                              string address = "127.0.0.1",
                              Information? bufferSize = null,
                              Duration? reconnectInterval = null,
                              Duration? timeout = null)
        {
            #region Check Params
            if (!bufferSize.HasValue)
                _bufferSize = Information.FromMegabytes(1);
            else _bufferSize = bufferSize.Value;

            if (!reconnectInterval.HasValue)
                _reconnectInterval = Duration.FromSeconds(1);

            else _reconnectInterval = reconnectInterval.Value;

            if (!timeout.HasValue)
                _timeout = Duration.FromSeconds(30);

            else _timeout = timeout.Value;
            #endregion

            if (port == 0)
                throw new ArgumentException("Port can't be 0");

            Port = port;

            IPAddress ip;
            if (IPAddress.TryParse(address, out ip))
                IP = ip;
            else
                throw new ArgumentException("Invalid IP address.");

            if (IsServer)
                CreateServer();
            else
                CreateClient();

        }

        #endregion

        #region API
        /// <summary>
        /// Send data (as <see cref="byte[]"/>).
        /// </summary>
        /// <remarks>this method will throw exception, in case of failure in <see cref="NetworkStream.Write(byte[], int, int)"/></remarks>
        /// <param name="data">the binary data to send</param>
        public void Send(byte[] data)
        {

            if (data == null)
                return;

            try
            {
                _nwStream?.Write(data, 0, data.Length);
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Close the communication
        /// </summary>
        public void Close()
        {
            _isOpen = false;
            _client?.Close();
            _listener?.Stop();
            OnConnectionChanged?.Invoke(false);
        }
        #endregion

        #region Private Methods

        private void CreateServer()
        {
            _localAdd = IP;
            try
            {
                _listener = new TcpListener(_localAdd, Port);
                _listener.Start();

                var runningThread = new Thread(CheckLiveliness);

                runningThread.IsBackground = true;
                runningThread.Start();
            }
            catch (Exception)
            {

                throw;
            }
        }

        private void CheckLiveliness()
        {
            var clock = Stopwatch.StartNew();

            while (_isOpen)
            {
                if (_client != null)
                {
                    if (IsConnected != _client.Connected)
                        IsConnected = _client.Connected;
                    if (_client.Connected && _nwStream.CanRead)
                        Read();
                    else
                        _client = null;

                    Thread.Sleep(100);
                }
                else
                {
                    var tryRestart = Task.Run(() => RestartServer());
                    bool isFinish = tryRestart.Wait(_timeout.ToTimeSpan());

                    if (clock.Elapsed > _timeout.ToTimeSpan())
                    {
                        Close();
                    }

                    Thread.Sleep(_reconnectInterval.ToTimeSpan());
                }
            }
        }

        private void CreateClient()
        {
            try
            {
                _client = new TcpClient(IP.ToString(), Port);
                _nwStream = _client.GetStream();
            }
            catch (Exception)
            {

                // actually, do nothing. it's noraml. sometimes client is being created withot matching server.
                // there is nothing wrong with that.
            }

            var workingThread = new Thread(LookForServerUntilTimeOut);

            workingThread.IsBackground = true;
            workingThread.Start();
        }

        private void CheckServer()
        {
            if (_client != null)
            {
                if (IsConnected != _client.Connected)
                {
                    IsConnected = _client.Connected;
                }

                if (IsConnected)
                {
                    Read();
                }
                else
                {
                    RestartClient();
                }

                Thread.Sleep(_reconnectInterval.ToTimeSpan());
            }

            // now, maybe the client is null
            if (_client == null)
            {
                IsConnected = false;
                RestartClient();
            }
        }

        private void Read()
        {
            try
            {
                byte[] buffer = new byte[_client.ReceiveBufferSize];
                _nwStream.Read(buffer, 0, _client.ReceiveBufferSize);

                OnDataArrived?.Invoke(buffer);
            }
            catch (Exception)
            {

                // do nothing. it's okay, we still love you.
            }
        }

        private void LookForServerUntilTimeOut()
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (_isOpen)
            {
                CheckServer();

                if (timer.Elapsed > _timeout.ToTimeSpan())
                {
                    Close();
                }
            }
        }

        private void RestartServer()
        {
            try
            {
                _client = _listener.AcceptTcpClient();
                ResetClient();
            }
            catch (Exception)
            {

                // do nothing
            }
        }

        private void RestartClient()
        {
            try
            {
                _client = new TcpClient(IP.ToString(), Port);
                ResetClient();
            }
            catch (Exception)
            {

                throw;
            }
        }

        private void ResetClient()
        {
            _client.ReceiveBufferSize = (int)_bufferSize.Bytes;
            _client.SendBufferSize = (int)_bufferSize.Bytes;
            _nwStream = _client.GetStream();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                _client.Close();
                _nwStream.Close();
            }
        }
        #endregion
    }
}
