using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comun
{
    public delegate void MessageReceivedDel(int cmd, byte[] data);
    public delegate void DisconnectDel();


    public class SocketManager
    {
        public Semaphore sendBlock = new Semaphore(1, 1);
        public const int LoginCmd = 1;
        public const int AlarmaLocalCmd = 10;
        public const int AlarmaRemotaCmd = 11;
        public const int PedidoLogCmd = 20;
        public const int PrefixSize = 11;


        private const int BufferLength = 1024;
        private Socket _Socket;
        public event MessageReceivedDel MessageRecived;
        public event DisconnectDel Disconnected;

        public SocketManager(IPAddress serverIP, int Port)
        {
            _Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPEndPoint remoteEndPoint = new IPEndPoint(serverIP, Port);
            _Socket.Connect(remoteEndPoint);
        }

        public SocketManager(Socket socket)
        {
            _Socket = socket;
        }

        class MessageState
        {
            public byte[] Buffer = new byte[BufferLength];
            public MemoryStream Data = new MemoryStream();
            public int DataSize = 0; //largo del mensaje a ser recibido
            public int Cmd = 0;
            public bool DataSizeReceived = false; //si se recibió el prefijo            
        }

        public string GetIP()
        {
            return _Socket.RemoteEndPoint.ToString();
        }

        public void SendMessage(byte[] message)
        {
            Thread envio = new Thread(new ParameterizedThreadStart(SendMessageThread));
            envio.Start(message);
        }

        public void SendMessageThread(object message)
        {
            try
            {
                sendBlock.WaitOne();
                _Socket.BeginSend((byte[])message, 0, ((byte[])message).Length, SocketFlags.None, new AsyncCallback(SendCallback), message);
            }
            catch (Exception)
            {
                //Debug.Fail(sex.ToString(), "SendMessage: Socket failed");
                if (Disconnected != null)
                {
                    Disconnected();
                }
                Close();
            }
        }

        private void SendCallback(IAsyncResult result)
        {
            try
            {
                byte[] message = (byte[])result.AsyncState;
                SocketError socketError;
                int dataSend = _Socket.EndSend(result, out socketError);

                if (socketError != SocketError.Success)
                {
                    Close();
                    return;
                }

                if (message != null && dataSend < message.Length)
                {
                    _Socket.BeginSend(message, dataSend, message.Length - dataSend, SocketFlags.None, new AsyncCallback(SendCallback), null);
                }
                else
                {
                    sendBlock.Release();
                }
            }
            catch (Exception)
            {
                //Debug.Fail(ex.ToString(), "SendCallback: Socket failed");
                if (Disconnected != null)
                {
                    Disconnected();
                }
                Close();
            }
        }

        public void StartReciving()
        {
            try
            {
                MessageState state = new MessageState();
                _Socket.BeginReceive(state.Buffer, 0, state.Buffer.Length, SocketFlags.None, new AsyncCallback(ReceiveCallback), state);
            }
            catch (SocketException)
            {
                //Debug.Fail(sex.ToString(), "StartReciving: Socket failed");
                if (Disconnected != null)
                {
                    Disconnected();
                }
                Close();
            }
        }

        private void ReceiveCallback(IAsyncResult result)
        {
            try
            {
                MessageState state = (MessageState)result.AsyncState;
                SocketError socketError;

                int dataRead = _Socket.EndReceive(result, out socketError);
                int dataOffset = 0;
                int restOfData = 0;

                if (socketError != SocketError.Success)
                {
                    if (Disconnected != null)
                    {
                        Disconnected();
                    }
                    Close();
                    return;
                }

                if (dataRead <= 0)
                {
                    if (Disconnected != null)
                    {
                        Disconnected();
                    }
                    Close();
                    return;
                }

                while (dataRead > 0)
                {
                    //chequeo para determinar si estoy leyendo el prefijo o los datos
                    if (!state.DataSizeReceived)
                    {
                        //ya hay informacion en el MemoryStream
                        if (state.Data.Length > 0)
                        {
                            restOfData = PrefixSize - (int)state.Data.Length;
                            if (restOfData > dataRead)
                            {
                                restOfData = dataRead;
                            }
                            state.Data.Write(state.Buffer, dataOffset, restOfData);
                            dataRead -= restOfData;
                            dataOffset += restOfData;
                        }
                        else if (dataRead >= PrefixSize)
                        {  //almaceno todo el prefijo en la memoria
                            state.Data.Write(state.Buffer, dataOffset, PrefixSize);
                            dataRead -= PrefixSize;
                            dataOffset += PrefixSize;
                        }
                        else
                        {  //almaceno solo parte del prefijo en la memoria
                            state.Data.Write(state.Buffer, dataOffset, dataRead);
                            dataOffset += dataRead;
                            dataRead = 0;
                        }

                        if (state.Data.Length == PrefixSize)
                        {  //se recibio todo el prefijo
                            state.Cmd = BitConverter.ToInt32(state.Data.GetBuffer(), 3);
                            state.DataSize = BitConverter.ToInt32(state.Data.GetBuffer(), 7);

                            state.DataSizeReceived = true;
                            //reseteo memoria             
                            state.Data.Position = 0;
                            state.Data.SetLength(0);
                        }
                        else
                        {  //se recibio solo parte del prefijo
                            //se inicia otra lectura
                            _Socket.BeginReceive(state.Buffer, 0, state.Buffer.Length, SocketFlags.None, new AsyncCallback(ReceiveCallback), state);
                            return;
                        }
                    }

                    //a este punto ya sabemos el largo del mensaje
                    if ((state.Data.Length + dataRead) >= state.DataSize)
                    {   //se tiene toda la informacion del mensaje

                        restOfData = state.DataSize - (int)state.Data.Length;

                        state.Data.Write(state.Buffer, dataOffset, restOfData);

                        if (MessageRecived != null)
                        {
                            MessageRecived(state.Cmd, state.Data.GetBuffer());
                        }

                        dataOffset += restOfData;
                        dataRead -= restOfData;

                        //mensaje recibido - se resetea la memoria
                        state.Data.SetLength(0);
                        state.Data.Position = 0;
                        state.DataSizeReceived = false;
                        state.DataSize = 0;
                        state.Cmd = 0;

                        if (dataRead == 0)
                        {  //no hay mas data en el buffer, se inicia un nuevo BeginReceive                                                       
                            _Socket.BeginReceive(state.Buffer, 0, state.Buffer.Length, SocketFlags.None, new AsyncCallback(ReceiveCallback), state);
                            return;
                        }
                        else
                            continue; //todavia queda data para procesar en el buffer
                    }
                    else
                    {  //todavia falta informacion para completar el mensaje, se almacena lo que se tiene, y se inicia un nuevo BeginReceive
                        state.Data.Write(state.Buffer, dataOffset, dataRead);

                        _Socket.BeginReceive(state.Buffer, 0, state.Buffer.Length, SocketFlags.None, new AsyncCallback(ReceiveCallback), state);
                        dataRead = 0;
                    }
                }
            }
            catch (Exception)
            {
                //Debug.Fail(ex.ToString(), "ReadCallback: Socket failed");
                if (Disconnected != null)
                {
                    Disconnected();
                }
                Close();
            }
        }

        public void Close()
        {
            if (_Socket == null)
            {
                return;
            }

            _Socket.Close();
            _Socket = null;
        }
    }
}
