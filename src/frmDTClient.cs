using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using OpenLayers.Base;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;

namespace Client
{
  public partial class frmDTClient : Form
  {
    const string IP = "127.0.0.1";
    const int Port = 26111;
    const int MaxSignals = 1000;

    delegate void Logger(string s);
    private Logger Log;

    bool testMode = false;

    List<double> coefficients;
    List<double> filteredSignals;
    bool coefficientsRead;
    bool coefficientSent;
    bool filterSuccessful;

    private delegate void LogDelegate(string s);
    LogDelegate LogMe;
    private delegate void VoidDelegate();
    private delegate void DoubleDelegate(List<double> data, List<double> data_in);

    Thread ServerProcessing;
    ClientComm comm = null;

    private static double xMin = 0;
    private static double xMax = 1000;
    private static double xStep = 100;
    private const double xMinRange = 100;
    private const double xMaxRange = 1000;
    private static double xRange = xMax - xMin;


    private bool stopAcquisition = false;

    public frmDTClient()
    {
      InitializeComponent();
      this.FormBorderStyle = FormBorderStyle.Fixed3D;
      Log += WriteToUILog;
      LogMe = WriteToUILog;
      filteredSignals = new List<double>(MaxSignals);
      dgvSignalData.Rows.Add(100);
      chartSignalData.ChartAreas[0].AxisX.Interval = 100;
      chartSignalData.ChartAreas[0].AxisX.MajorTickMark.Interval = 100;
      chartSignalData.ChartAreas[0].AxisX.MinorTickMark.Interval = 50;
      chartSignalData.ChartAreas[0].CursorX.IsUserSelectionEnabled = true;
      chartSignalData.Series[0].Points.Clear();
      chartSignalData.Series[1].Points.Clear();
    }


    private void btnStartStop_Click(object sender, EventArgs e)
    {
      if (coefficientsRead == true)
      {
        try
        {
          if (btnStartStop.Text == "START")
          {
            comm = new ClientComm(Port, IP);
            btnStartStop.Text = "STOP";

            if (testMode)
            {
              ServerProcessing = new Thread(GetFilteredTestData);
              ServerProcessing.Start();
            }
            else
            {
              ServerProcessing = new Thread(GetFilteredData);
              ServerProcessing.Start();
            }
          }
          else
          {
            stopAcquisition = true;
            btnStartStop.Text = "START";
            return;
          }
        }
        catch (Exception ex)
        {
          Log("Error in connection: " + ex.Message);
        }
      }
      else
      {
        MessageBox.Show("No coefficient file specified.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void DoubleToBuffer(List<double> values, byte[] buffer)
    {
      for (int i = 0; i < values.Count; i++)
      {
        byte[] temp = BitConverter.GetBytes(values[i]);
        for (int j = 0; j < sizeof(double); j++)
          buffer[i*sizeof(double)+j] = temp[j];
      }
    }

    private void BufferToDouble(byte[] buffer, List<double> values)
    {
      for (int i = 0; i < buffer.Length; i += sizeof(double))
        values.Add(BitConverter.ToDouble(buffer, i));
    }

    private void GetFilteredData()
    {
      string msg = "";
      int numBuffers = 0;
      StreamWriter signalWriter = new StreamWriter("filtered_signals.txt");
      List<double> bufferedSignals = new List<double>();
      List<double> bufferedSignals_in = new List<double>();
      bool switchChanged;
      bool paused = false;
      double average, variance, min, max;
      
      filterSuccessful = false;

      Log("====REAL-TIME ACQUISITION====");

      try
      {
        if (!coefficientSent)
        {
          byte[] buffer = new byte[coefficients.Count * sizeof(double)];
          DoubleToBuffer(coefficients, buffer);
          comm.Send("COEF");
          comm.Send(ref buffer);
          Log("+OK  Coefficients sent successfully.");
          coefficientSent = true;
        }
      }
      catch (Exception ex)
      {
        Log("ERR  Failed on coefficients: " + ex.Message);
      }

      try
      {
        comm.Send("INIT");
        comm.Send(double.Parse(tbSampleRate.Text));
        Thread.Sleep(100);  // Let the server initialize the device
        comm.Send("START");

        while (true)
        {
          byte[] buffer;
          comm.Receive(out switchChanged);
          if (switchChanged)
          {
            comm.Receive(out msg);
            if (msg.Contains("PAUSE"))
            {
              Log("Signal Processing Paused.");
              paused = true;
              continue;
            }
            else
              paused = false;
          }
          else
          {
            if (paused)
            {
              Thread.Sleep(100);
              continue;
            }
          }

          comm.Receive(out buffer);
          BufferToDouble(buffer, bufferedSignals_in);
          comm.Receive(out buffer);
          BufferToDouble(buffer, bufferedSignals);
          comm.Receive(out average);
          comm.Receive(out variance);
          comm.Receive(out min);
          comm.Receive(out max);
          
          
          numBuffers++;

          Log += signalWriter.WriteLine;
          Log(String.Format("Buffer {0}", numBuffers));
          Log("**************");
          Log -= signalWriter.WriteLine;
          foreach (double signal in bufferedSignals)
          {
            filteredSignals.Add(signal);
            signalWriter.WriteLine(signal);
          }
          Log += signalWriter.WriteLine;

          if (filteredSignals.Count >= xMax)
          {
            UpdateDisplayElements(filteredSignals, bufferedSignals_in);
            filteredSignals.Clear();
          }

          Log(String.Format("Average = {0}", average));
          Log(String.Format("Variance = {0}", variance));
          Log(String.Format("Min = {0}", min));
          Log(String.Format("Max = {0}", max));
          Log("======================================");
          Log("");
          Log -= signalWriter.WriteLine;

          bufferedSignals.Clear();
          bufferedSignals_in.Clear();

          if (stopAcquisition)
          {
            comm.Send("STOP");          
            comm.Close();
            break;
          }
          else
            comm.Send("CONT");
        }

        Log("Signals filtered successfully.");
        filterSuccessful = true;
      }
      catch (Exception ex)
      {
        Log("ERR  Realtime Acquisition failed: " + ex.Message);
      }
      finally
      {
        signalWriter.Close();
      }
    }

    private void GetFilteredTestData()
    {
      string msg;
      int numBuffers = 0;
      StreamWriter signalWriter = new StreamWriter("filtered_signals.txt");
      List<double> bufferedSignals = new List<double>();
      List<double> bufferedSignals_in = new List<double>();
      double average, variance, min, max;
      filterSuccessful = false;
      
      Log("====RUNNING TEST====");

      try
      {
        if (!coefficientSent)
        {
          byte[] buffer = new byte[coefficients.Count * sizeof(double)];
          DoubleToBuffer(coefficients, buffer);
          comm.Send("COEF");
          comm.Send(ref buffer);
          Log("+OK  Coefficients sent successfully.");
          coefficientSent = true;
        }
      }
      catch (Exception ex)
      {
        Log("ERR  Failed on coefficients: " + ex.Message);
      }

      try
      {
        comm.Send("TEST");

        Log("Signal test began...");
        while (true)
        {
          comm.Receive(out msg);
          if (msg.Contains("DONE"))
            break;
          byte[] buffer;
          comm.Receive(out buffer);
          BufferToDouble(buffer, bufferedSignals);
          comm.Receive(out buffer);
          BufferToDouble(buffer, bufferedSignals_in);
          comm.Receive(out average);
          comm.Receive(out variance);
          comm.Receive(out min);
          comm.Receive(out max);
          
          numBuffers++;

          Log += signalWriter.WriteLine;
          Log(String.Format("Buffer {0}", numBuffers));
          Log("**************");
          Log -= signalWriter.WriteLine;
          foreach (double signal in bufferedSignals)
          {
            filteredSignals.Add(signal);
            signalWriter.WriteLine(signal);
          }
          Log += signalWriter.WriteLine;

          if (filteredSignals.Count >= 6000)
          {
            UpdateDisplayElements(filteredSignals, bufferedSignals_in);
            filteredSignals.Clear();
            bufferedSignals_in.Clear();
          }

          Log(String.Format("Average = {0}", average));
          Log(String.Format("Variance = {0}", variance));
          Log(String.Format("Min = {0}", min));
          Log(String.Format("Max = {0}", max));
          Log("======================================");
          Log("");
          Log -= signalWriter.WriteLine;

          
          bufferedSignals.Clear();
        }

        Log("Signals filtered successfully.");
        filterSuccessful = true;
      }
      catch (Exception ex)
      {
        Log("ERR  Signal test failed: " + ex.Message);
      }
      finally
      {
        signalWriter.Close();
      }
    }

    private void UpdateDisplayElements(List<double> data, List<double> data_in)
    {
      if (this.InvokeRequired)
        this.Invoke(new DoubleDelegate(UpdateDisplayElements), new object [] {data, data_in});
      else
      {
        chartSignalData.Series[0].Points.Clear();
        chartSignalData.Series[1].Points.Clear();
        for (int i = 0; i < data.Count; i++)
        {
          if (i < 100)
            dgvSignalData.Rows[i].SetValues(i + 1, data[i]);

          if (i < xRange)
          {
            chartSignalData.Series[0].Points.AddXY(i, data[i]);
            chartSignalData.Series[1].Points.AddXY(i, data_in[i]);
          }
        }
        chartSignalData.ChartAreas[0].AxisX.Minimum = xMin;
        chartSignalData.ResetAutoValues();
      }
    }

    private void WriteToUILog(string s)
    {
      if (this.InvokeRequired)
        this.Invoke(LogMe, new object[] { s });
      else
        tbLog.AppendText(s + "\r\n");
    }

    private void Form1_FormClosed(object sender, FormClosedEventArgs e)
    {
    }

    private void btnCoefFile_Click(object sender, EventArgs e)
    {
      OpenFileDialog ofd = new OpenFileDialog();
      StreamReader reader;
      NumberStyles ns = NumberStyles.Float;


      if (ofd.ShowDialog() == DialogResult.OK)
      {
        coefficients = new List<double>();
        try
        {
          if ((reader = new StreamReader(ofd.OpenFile())) != null)
          {
            using (reader)
            {
              while (reader.EndOfStream == false)
              {
                coefficients.Add(Double.Parse(reader.ReadLine(), ns));
              }
              coefficientsRead = true;
            }
          }
        }
        catch (Exception ex)
        {
          MessageBox.Show("Error: Could not read file from disk. Original error: " + ex.Message);
        }

        tbCoefFile.Text = "+OK Coefficients Read Successfully.";
      }
    }

    private void cbTest_CheckedChanged(object sender, EventArgs e)
    {
      testMode = (testMode) ? false : true;
    }

    private void frmDTClient_Load(object sender, EventArgs e)
    {
    }

    private void button2_Click(object sender, EventArgs e)
    {
      xRange = xMax - xMin;
      xMin = (xRange >= xMaxRange) ? xMin : xMin - xStep;
    }

    private void button1_Click(object sender, EventArgs e)
    {
      xRange = xMax - xMin;
      xMin = (xRange <= xMinRange) ? xMin : xMin + xStep;
    }

  }

  class ClientComm
  {
    private int m_port;
    private IPAddress m_IP;
    private TcpClient m_client;
    private NetworkStream m_netStream;
    private BinaryWriter m_writer;
    private BinaryReader m_reader;

    private const int FailureLimit = 3;
    private const int ResponseTimeout = 5000;

    private bool RcvAck()
    {
      try
      {
        string ackString = m_reader.ReadString();

        if (ackString.Contains("ACK"))
          return true;
        else
          return false;
      }
      catch (IOException ioex)  //means the read exceeded timeout
      {
        return false;
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public ClientComm(int port, string ipAddress)
    {
      try
      {
        m_port = port;
        m_IP = IPAddress.Parse(ipAddress);
        m_client = new TcpClient();
        m_client.Connect(m_IP, m_port);
        m_netStream = m_client.GetStream();
        m_reader = new BinaryReader(m_netStream);
        m_writer = new BinaryWriter(m_netStream);

        m_netStream.ReadTimeout = ResponseTimeout;
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    ~ClientComm() { Close(); }

    public void TurnOffTimeout()
    {
      m_netStream.ReadTimeout = -1;
    }

    public bool Send(string command)
    {
      try
      {
        for (int i = 0; i < FailureLimit; i++)
        {
          m_writer.Write(command);
          if (RcvAck())
            return true;
        }

        return false;
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public bool Send(ref byte[] buffer)
    {
      try
      {
        for (int i = 0; i < FailureLimit; i++)
        {
          m_writer.Write(buffer.Length);
          m_writer.Write(buffer);
          if (RcvAck())
            return true;
        }

        return false;
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public bool Send(double value)
    {
      try
      {
        m_writer.Write(value);
        if (RcvAck())
          return true;
        return false;
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void Receive(out string message)
    {
      try
      {
        message = m_reader.ReadString();
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void Receive(out byte[] buffer)
    {
      try
      {
        int length = m_reader.ReadInt32();
        if (length > 800)
          buffer = new byte[800];
        buffer = new byte[length];
        buffer = m_reader.ReadBytes(length);
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void Receive(out bool value)
    {
      try
      {
        value = m_reader.ReadBoolean();
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void Receive(out double value)
    {
      try
      {
        value = m_reader.ReadDouble();
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void Close()
    {
      if (m_reader != null)
        m_reader.Close();

      if (m_writer != null)
      {
        m_writer.Close();
      }

      if (m_netStream != null)
        m_netStream.Close();

      if (m_client != null)
        m_client.Close();
    }
  }
}
