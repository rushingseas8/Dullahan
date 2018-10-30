﻿using Dullahan.Logging;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;

namespace Dullahan.Net
{
	/// <summary>
	/// Manages the connection to a Dullahan server/client
	/// </summary>
	public class Client : ILogWriter, ILogReader
	{
		#region STATIC_VARS

		public const int DEFAULT_PORT = 8080;

		private const string DEBUG_TAG = "[CLIENT]";

		private const int DB_LENGTH = 1024;
		#endregion

		#region INSTANCE_VARS

		public string Name { get; set; }

		private readonly object stateLock = new object();

		/// <summary>
		/// Connected to the remote host.
		/// </summary>
		public bool Connected { get; private set; }

		/// <summary>
		/// Receiving data from the remote host.
		/// </summary>
		public bool Reading { get; private set; }

		/// <summary>
		/// Sending data to the remote host.
		/// </summary>
		public bool Sending { get; private set; }

		/// <summary>
		/// Was connected and lost connection, or attempted connection failed.
		/// </summary>
		public bool Disconnected { get; private set; }

		/// <summary>
		/// The availability state of the Client. 
		/// Returns true if connected and no operations are underway.
		/// </summary>
		public bool Idle
		{
			get
			{
				lock(stateLock)
					return !Disconnected && Connected && !Reading && !Sending;
			}
		}

		/// <summary>
		/// Triggers when a read operation has finished, and data is available for use
		/// </summary>
		public event DataReceivedCallback dataRead;

		private int port;

		private IPAddress address;

		private TcpClient client;
		private NetworkStream stream;

		/// <summary>
		/// Data received
		/// </summary>
		private List<byte> storedData;

		
		#endregion

		#region STATIC_METHODS

		#endregion

		#region INSTANCE_METHODS

		/// <summary>
		/// Create a new Client object that needs to be connected to a remote endpoint
		/// </summary>
		/// <param name="address">The address to which connection wiil be attempted</param>
		/// <param name="port">The port, what else?</param>
		public Client(IPAddress address, int port = DEFAULT_PORT) : this()
		{
			this.address = address;
			this.port = port;

			Connected = Reading = Sending = false;

			client = new TcpClient();
			stream = null;
		}

		/// <summary>
		/// Create a new Client objct with an existing and connected TcpClient
		/// </summary>
		/// <param name="existingClient"></param>
		public Client(TcpClient existingClient) : this()
		{
			client = existingClient;
			stream = client.GetStream();

			Connected = true;
			Reading = Sending = false;

			address = null;
			port = -1;
		}

		private Client()
		{
			storedData = new List<byte> ();
		}

		/// <summary>
		/// If the Client is not connected to an endpoint, try connecting.
		/// This function is async.
		/// </summary>
		public void Start()
		{
			if (!Connected)
			{
				//establish connection
				client.BeginConnect(address, port, ConnectFinished, client);
			}
		}

		private void ConnectFinished(IAsyncResult res)
		{
			try
			{
				stream = client.GetStream ();
			}
			catch (InvalidOperationException)
			{
				//failed connection for some reason
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine ("Could not connect to server at " + address.ToString() + ":" + port);
				Console.ResetColor ();
				Disconnected = true;
				return;
			}

			//connection established
			Connected = true;
		}

		/// <summary>
		/// Read from the currently open connection
		/// </summary>
		public void Read()
		{
			if (stream != null)
			{
				//begin reading operation
				byte[] dataBuffer = new byte[DB_LENGTH];
				stream.BeginRead(dataBuffer, 0, dataBuffer.Length, ReadFinished, dataBuffer);

				Reading = true;
			}
		}

		/// <summary>
		/// Read from the server has finished
		/// </summary>
		/// <param name="res"></param>
		private void ReadFinished(IAsyncResult res)
		{
			byte[] dataBuffer = (byte[])res.AsyncState;
			int byteC = stream.EndRead (res);
			
#if DEBUG
			Console.WriteLine (DEBUG_TAG + " Read " + byteC + "B");
#endif

			//add new data in the buffer to the store
			for (int i = 0; i < byteC; i++)
				storedData.Add(dataBuffer[i]);

			//more data to read
			if (stream.DataAvailable)
			{
#if DEBUG
				Console.WriteLine(DEBUG_TAG + " More data, continuing read.");
#endif
				stream.BeginRead(dataBuffer, 0, dataBuffer.Length, ReadFinished, dataBuffer);
			}
			//all data read, resolve packet and notify listeners
			else
			{
				Packet data = null;
				try
				{
					MemoryStream ms = new MemoryStream();
					BinaryFormatter formatter = new BinaryFormatter();
					byte[] sd = storedData.ToArray();
					ms.Write(sd, 0, sd.Length);
					ms.Seek(0, SeekOrigin.Begin);
					data = (Packet)formatter.Deserialize(ms);
				}
				catch(Exception e)
				{
					Console.Error.Write(e);
					Send(new Packet(Packet.DataType.response, "Error reading provided packet.\n" + e.Message));
				}

				storedData.Clear();
				Reading = false;
#if DEBUG
				Console.WriteLine(DEBUG_TAG + " Finished read");
#endif
				if (data.type == Packet.DataType.management)
				{
					//consume packet internally
					HandleManagementPacket(data);
				}
				else
				{
					//notify listeners of new packet
					if (dataRead != null && data != null)
						dataRead(this, data);
				}
			}
		}

		private void HandleManagementPacket(Packet packet)
		{
			string[] data = packet.data.Split (' ');

			try
			{
				switch (data[0])
				{
					//inital handshake where the server names the connecting client, and 
					//passes the name back over the network
					case "setup":
						if (Name != null && Name != "")
							Send(new Packet(Packet.DataType.management, "setup_resp " + Name));
						break;
					case "setup_resp":
						Name = data[1];
#if DEBUG
						Console.WriteLine(DEBUG_TAG + " Received name: " + Name);
#endif
						break;
				}
			}
			catch(Exception e)
			{
				Console.WriteLine(DEBUG_TAG + " There was some error in handling a management packet\n" + e.Message);
			}
		}

		public void Send(Packet packet)
		{
			if (stream != null)
			{
#if DEBUG
				Console.WriteLine(DEBUG_TAG + " Sending \"" + packet.data + "\"");
#endif
				//convert packet into binary data
				byte[] sendBytes;
				BinaryFormatter formatter = new BinaryFormatter();
				using (MemoryStream ms = new MemoryStream())
				{
					formatter.Serialize(ms, packet);
					sendBytes = ms.ToArray();
				}

				//begin send operation
				stream.BeginWrite(sendBytes, 0, sendBytes.Length, SendFinished, stream);

				Sending = true;
			}
		}

		private void SendFinished(IAsyncResult res)
		{
			NetworkStream stream = (NetworkStream)res.AsyncState;
			stream.EndWrite (res);

#if DEBUG
			Console.WriteLine (DEBUG_TAG + " Finished sending");
#endif
			Sending = false;
		}

		/// <summary>
		/// Perform a Send and block for the response
		/// </summary>
		/// <param name="outbound"></param>
		/// <returns></returns>
		public bool SendAndRead(Packet outbound, DataReceivedCallback onResult)
		{
			if(onResult != null)
				dataRead += onResult;

			Send(outbound);
			while (Sending) { }
			Read();
			while (Reading) { }

			if(onResult != null)
				dataRead -= onResult;

			return true;
		}
		public bool SendAndWait(Packet outbound)
		{
			return SendAndRead(outbound, null);
		}

		public void Disconnect()
		{
			stream.Close();
			client.Close();
			Sending = Reading = Connected = false;
			Disconnected = true;
		}

		public override int GetHashCode()
		{
			return Name.GetHashCode();
		}

		public override bool Equals(object obj)
		{
			return Name.Equals(((Client)obj).Name);
		}

		#region INTERFACE_METHODS
		public void Write(Message msg)
		{
			//TODO send the message to the remote host
			throw new NotImplementedException ();
		}

		public string ReadLine()
		{
			//TODO signal to the remote host that input is expected
			throw new NotImplementedException ();
		}
		#endregion
		#endregion

		#region INTERNAL_TYPES

		public delegate void DataReceivedCallback(Client endpoint, Packet data);
		#endregion
	}
}
