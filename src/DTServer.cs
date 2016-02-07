using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using OpenLayers.Base;
using System.Collections.Generic;
using System.Threading.Tasks;
using DT9816;

namespace DTServer
{
  class Server
  {
    private int m_port;
    private IPAddress m_IP;
    private int m_maxConnections;
    private int m_numConnections;
    private bool m_stopServer;
    BinaryReader request;
    BinaryWriter response;
    private bool[] m_hasConnection;
    private Thread[] m_connectionThreads;
    private List<double> m_coefficients;
    private double[] m_signals;
    private double[] m_buffer;
    private double[] m_monitorBuffer;


    private bool processSignals = false;
    private bool stopped = false;
    private bool firstTime = true;

    private const int BufferSize = 100; // not a good idea to change this
    private const int SampleSize = 1000;

    private DTControl m_device;
    private const int SignalChannel = 0;

    public delegate void logger(string s);
    public logger Log;

    double average, variance, min, max;
    double[] filteredSignals;

    StreamWriter signalWriter;

    private void DoubleToBuffer(List<double> values, byte[] buffer)
    {
      for (int i = 0; i < values.Count; i++)
      {
        byte[] temp = BitConverter.GetBytes(values[i]);
        for (int j = 0; j < sizeof(double); j++)
          buffer[i * sizeof(double) + j] = temp[j];
      }
    }

    private void DoubleToBuffer(double[] values, byte[] buffer)
    {
        for (int i = 0; i < values.Length; i++)
        {
            byte[] temp = BitConverter.GetBytes(values[i]);
            for (int j = 0; j < sizeof(double); j++)
                buffer[i * sizeof(double) + j] = temp[j];
        }
    }

    private void BufferToDouble(byte[] buffer, List<double> values)
    {
      for (int i = 0; i < buffer.Length; i += sizeof(double))
        values.Add(BitConverter.ToDouble(buffer, i));
    }

    private void GetBuffer(ref double[] buf)
    {
      m_buffer = new double[SampleSize];
      m_monitorBuffer = new double[SampleSize];
      filteredSignals = new double[BufferSize];
      byte[] bufferedSignal = new byte[SampleSize*sizeof(double)];
      List<double> wholeBuffer = new List<double>(SampleSize);
      bool switchChanged = false;

      if (stopped)
        return;

      // separate the signal from the switch data
      for (int i = 0; i < buf.Length; i += 2)
      {
        m_buffer[i/2] = buf[i];
        m_monitorBuffer[i/2] = buf[i + 1];
      }


      if (m_monitorBuffer[0] > 3.3)
      {
          for (int signalIndex = 0; signalIndex < m_buffer.Length; signalIndex += BufferSize)
          {
              double[] buffer = new double[BufferSize];
              // copy 100 values unless the buffer size is less than 100 (i.e. the last buffer only has 1 value)
              Array.Copy(m_buffer, signalIndex, buffer, 0, ((m_buffer.Length - signalIndex) < BufferSize) ? m_buffer.Length - signalIndex : BufferSize);
              ProcessSignals(buffer, out filteredSignals, out average, out variance, out min, out max);
              foreach (double signal in filteredSignals)
                  wholeBuffer.Add(signal);
              Log("Signals Processed.");
          }

          if (!stopped)
          {
            if (processSignals == false)
              switchChanged = true;

            if (switchChanged)
            {
              response.Write(switchChanged);
              response.Write("CONT");
            }
            else
              response.Write(switchChanged);

            //write signals to file
            foreach (double signal in m_buffer)
              signalWriter.WriteLine(signal);

            // Input Signals
            DoubleToBuffer(m_buffer, bufferedSignal);
            response.Write(bufferedSignal.Length);
            response.Write(bufferedSignal);
            // Filtered Signals
            DoubleToBuffer(wholeBuffer, bufferedSignal);
            response.Write(bufferedSignal.Length);
            response.Write(bufferedSignal);
            response.Write(average);
            response.Write(variance);
            response.Write(min);
            response.Write(max);
            string msg = request.ReadString();
            response.Write("+ACK");
            Log("Buffer sent");
            if (msg == "STOP")
            {
              m_device.PutDoutValue(3);
              m_device.AnalogStop();
              m_device.ClearQueue();
              stopped = true;
              return;
            }
          }
      }
      else
      {
        if (processSignals == true || firstTime)
        {
          switchChanged = true;
          firstTime = false;
        }

        if (switchChanged)
        {
          response.Write(switchChanged);
          response.Write("PAUSE");
        }
        else
          response.Write(switchChanged);

      }
      processSignals = (m_monitorBuffer[0] > 3.3) ? true : false;
    }

    private bool ProcessSignals(double[] buffer, out double[] result, out double average, out double variance, out double min, out double max)
    {
      int signalStart = BufferSize;
      double[] filteredSignals = new double[buffer.Length];
      object mutex = new object();
      double sum = 0.0;
      min = max = average = variance = 0.0;
      result = filteredSignals;

      // copy new signals into upper half of buffer
      for (int i = 0; i < buffer.Length; i++)
        m_signals[signalStart + i] = buffer[i];

      Parallel.For(0, buffer.Length,
      (idx) =>
      {
        for (int i = 0; i < m_coefficients.Count; i++)
        {
          filteredSignals[idx] += m_coefficients[i] * m_signals[signalStart + idx - i];
        }

        // can't have multiple threads simultaneously accessing the shared data
        lock (mutex)
          sum += filteredSignals[idx];
      });
      average = sum / filteredSignals.Length;
      min = max = filteredSignals[0];
      foreach (double signal in filteredSignals)
      {
        min = (signal < min) ? signal : min;
        max = (signal > max) ? signal : max;
        variance += Math.Pow(signal - average, 2);
      }

      variance /= filteredSignals.Length;

      //shift new signals to lower half
      for (int i = 0; i < buffer.Length; i++)
        m_signals[i] = m_signals[signalStart + i];
      
      return true;
    }


    private void Communicate(int aConn, ref NetworkStream ns)
    {
      request = new BinaryReader(ns);
      response = new BinaryWriter(ns);
      
      m_hasConnection[aConn] = true;

      try
      {
        while (!m_stopServer && m_hasConnection[aConn])
        {
          string cmd;
          string msg = "";

          while (!ns.DataAvailable) 
          {
            if (m_stopServer)
              return;
            Thread.Sleep(100); 
          }

          cmd = request.ReadString();

          switch (cmd)
          {
            case "START":
              {
                Log("Acquisition started");
                response.Write("+ACK");
                m_signals = new double[BufferSize * 2];
                byte[] bufferedSignal = new byte[BufferSize * sizeof(double)];
                m_device.PutDoutValue(1);
                signalWriter = new StreamWriter("real_signals.txt");
                stopped = false;

                try
                {
                  m_device.AnalogStart();
                  //wait for it to start (maybe not necessary)
                  while (m_device.AnalogRunning() == false) { }

                  while (m_device.AnalogRunning() && !stopped)
                  {
                    Thread.Sleep(500);
                  }
                  
                  if (!stopped)
                    m_hasConnection[aConn] = false;
                  msg = "+OK  Signals filtered.";
                  
                }
                catch (Exception ex)
                {
                  Log("Signal filtering failed: " + ex.Message);
                }
                break;
              }
            case "STOP":
              response.Write("+ACK");
              m_device.PutDoutValue(3);
              if (m_device.AnalogRunning())
                m_device.AnalogStop();
              response.Write("DONE");

              break;
              
            case "CLOSE":
              msg = "Connection Closed.";
              m_hasConnection[aConn] = false;
              break;

            case "COEF":
              msg = "+ACK  COEF";
              response.Write(msg);
              try
              {
                int length = request.ReadInt32();
                byte[] buffer = request.ReadBytes(length);
                BufferToDouble(buffer, m_coefficients);
              }
              catch (Exception ex)
              {
                msg = "-ERR  Failed to receive coefficients. " + ex.Message;
                Log(String.Format("({0}) {1}", aConn, msg));
              }
              msg = "+ACK  coefficients received.";
              response.Write(msg);
              break;

            case "TEST":
              {
                msg = "+ACK TEST command received. Filtering test signal.";
                response.Write(msg);

                //read test signals
                StreamReader testReader = new StreamReader(@"..\..\signal.txt");
                List<double> testSignals = new List<double>();
                while (testReader.EndOfStream == false)
                {
                  testSignals.Add(Double.Parse(testReader.ReadLine()));
                }
                Log("+OK Signals read successfully.");

                byte[] bufferedSignals = new byte[BufferSize * sizeof(double)];
                double[] filteredSignals;
                double average, variance, min, max;
                int bufferIndex = 0;

                // this part is weird, because the test file is 6001 signals, but we want to send 100-length buffers
                // 
                for (int signalIndex = 0; signalIndex < testSignals.Count; signalIndex += BufferSize)  //simulate 100-length buffers
                {
                  double[] buffer = new double[BufferSize];

                  // copy 100 values unless the buffer size is less than 100 (i.e. the last buffer only has 1 value)
                  testSignals.CopyTo(signalIndex, buffer, 0, ((testSignals.Count - signalIndex) < BufferSize) ? testSignals.Count - signalIndex : BufferSize);
                  if (!ProcessSignals(buffer, out filteredSignals, out average, out variance, out min, out max))
                    continue;
                  DoubleToBuffer(filteredSignals, bufferedSignals);
                  response.Write("BUF");
                  response.Write(bufferedSignals.Length);
                  response.Write(bufferedSignals);
                  DoubleToBuffer(buffer, bufferedSignals);
                  response.Write(bufferedSignals.Length);
                  response.Write(bufferedSignals);
                  response.Write(average);
                  response.Write(variance);
                  response.Write(min);
                  response.Write(max);
                  bufferIndex++;
                }
                response.Write("DONE");
                msg = "+OK: Signals filtered.";
                break;
              }

            case "INIT":
              response.Write("+ACK");
              if (m_device == null)
              {
                double frequency = request.ReadDouble();
                m_device = new DTControl(frequency, new int[] { 0, 1 }, new DTControl.Logger(Log), new DTControl.DoneSignalHandler(GetBuffer));
                m_device.PutDoutValue(0);
                msg = "+OK DT9816 Initialized.";
              }
              else
                msg = "+OK DT9816 Already initialized.";
              break;

            default:
              msg = "-ERR: Command not recognized.";
              response.Write(msg);
              break;
          }

          response.Flush();
          Log(String.Format("({0}) {1}", aConn, msg));
        }
      }
      catch (Exception ex)
      {
        Log(String.Format("[{0} {1}] -ERR  Connection {2} failed: {3}",
            DateTime.Now.ToShortDateString(), DateTime.Now.ToLongTimeString(), aConn, ex.Message));
      }
      finally
      {
        ns.Close();
      }

    }


    public Server(int aPort, int maxConn = 10)
    {
      m_port = aPort;
      m_IP = IPAddress.Any;
      m_maxConnections = maxConn;
      m_numConnections = 0;
      m_stopServer = false;
      m_signals = new double[BufferSize * 2];
      m_coefficients = new List<double>();
    }

    public void Run()
    {
      TcpListener listener = new TcpListener(m_IP, m_port);
      m_connectionThreads = new Thread[m_maxConnections];
      m_hasConnection = new bool[m_maxConnections];
      listener.Start();
      Log(String.Format("+OK  Server Listening on {0}.", m_port));

      try
      {
        while (!m_stopServer)
        {
          // if there is a connection request, open a new stream
          // and create a thread to process the connection
          if (listener.Pending())
          {
            NetworkStream ns = listener.AcceptTcpClient().GetStream();

            // find a free connection
            int i;
            for (i = 0; i < m_maxConnections; ++i)
            {
              if (!m_hasConnection[i])
                break;
            }

            if (i == m_maxConnections)
            {
              BinaryWriter w = new BinaryWriter(ns);
              w.Write("-ERR: Maximum connections reached.");
              Log(String.Format("-ERR: Maximum connections reached.", i));
              w.Flush();
              ns.Close();
              continue;
            }

            m_connectionThreads[i] = new Thread(() => Communicate(i, ref ns));
            m_connectionThreads[i].Start();
            Log(String.Format("+OK  Connection {0} created.", i));

            m_numConnections++;
          }
          else { Thread.Sleep(100); }
        }

      }
      catch (Exception ex)
      {
        Log(String.Format("-ERR  Server failed: {0}", ex.Message));
        throw ex;
      }
      finally
      {
        listener.Stop();
      }

    }

    public void Stop()
    {
      m_stopServer = true;

      for (int i = 0; i < m_numConnections; ++i)
      {
        m_hasConnection[i] = false;
        if (m_connectionThreads[i] != null && m_connectionThreads[i].ThreadState != ThreadState.Stopped)
          m_connectionThreads[i].Join();
        Log(String.Format("+OK  Connection {0} stopped.", i));
      }
      Log(String.Format("+OK  Server stopped."));
    }
  }
}
