using System;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using OpenLayers.Base;

namespace DT9816
{
  class DTControl
  {
    private Device m_device;
    private DeviceMgr m_deviceMgr;
    private AnalogInputSubsystem m_ainSS;
    private DigitalInputSubsystem m_dinSS;
    private DigitalOutputSubsystem m_doutSS;
    private OlBuffer[] m_daqBuffers;

    private double m_frequency;
    private const int MaxBuffers = 8;
    private const int SampleSize = 2000;

    private List<int> m_physicalChannels;
    private int m_outMask = 0;
    private int m_buffersComplete;

    public delegate void Logger(string s);
    public Logger Log;

    public delegate void DoneSignalHandler(ref double[] buf);
    public DoneSignalHandler doneSignalHandler;

    public DTControl(double frequency, int[] analogChannels, Logger log, DoneSignalHandler callback)
    {
      try
      {
        m_deviceMgr = DeviceMgr.Get();

        if (!m_deviceMgr.HardwareAvailable())
          throw new Exception("No Devices Available.");

        // Get first available device
        m_device = m_deviceMgr.GetDevice(m_deviceMgr.GetDeviceNames()[0]);

        // Get subsystems
        m_ainSS = m_device.AnalogInputSubsystem(0);
        m_dinSS = m_device.DigitalInputSubsystem(0);
        m_doutSS = m_device.DigitalOutputSubsystem(0);

        /*
         * ANALOG SETUP
         */

        //Add event handlers
        m_ainSS.DriverRunTimeErrorEvent += HandleDriverRunTimeErrorEvent;
        m_ainSS.BufferDoneEvent += HandleBufferDone;
        m_ainSS.QueueDoneEvent += HandleQueueDone;
        m_ainSS.QueueStoppedEvent += HandleQueueStopped;

        // Set frequency
        m_frequency = (m_ainSS.Clock.MaxFrequency < frequency) ? m_ainSS.Clock.MaxFrequency : frequency;
        m_ainSS.Clock.Frequency = m_frequency;
        m_ainSS.VoltageRange = new Range(-10, 10);

        // Setup buffers
        m_ainSS.BufferQueue.FreeAllQueuedBuffers(); //just in case some are in the queue
        m_daqBuffers = new OlBuffer[MaxBuffers];
        for (int i = 0; i < MaxBuffers; i++)
        {
          // Allocate and place each buffer in queue
          m_daqBuffers[i] = new OlBuffer(SampleSize, m_ainSS);
          m_ainSS.BufferQueue.QueueBuffer(m_daqBuffers[i]);
        }

        // Set for continuous operation
        m_ainSS.DataFlow = DataFlow.Continuous;

        // Set channel list
        m_ainSS.ChannelList.Clear();
        m_physicalChannels = new List<int>();
        foreach (int channel in analogChannels)
        {
          ChannelListEntry channelListEntry = new ChannelListEntry(m_ainSS.SupportedChannels.GetChannelInfo(SubsystemType.AnalogInput, channel));
          channelListEntry.Gain = 1.0;
          m_ainSS.ChannelList.Add(channelListEntry);
          m_physicalChannels.Add(channel);
        }

        // Save configuration
        m_ainSS.Config();

        /*
         * DIGITAL SETUP
         */
        m_dinSS.DataFlow = DataFlow.SingleValue;
        m_doutSS.DataFlow = DataFlow.SingleValue;

        m_dinSS.Config();
        m_doutSS.Config();

        doneSignalHandler += callback;

        Log = log;

        Log("DT9816 and all subsystems initialized.");

        // Display actual hardware frequency set
        Log(String.Format("Actual Hardware Frequency = {0:0.000}", m_ainSS.Clock.Frequency));
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void ClearQueue()
    {
        if (m_ainSS != null)
            m_ainSS.BufferQueue.FreeAllQueuedBuffers();
    }

    public bool AnalogRunning()
    {
      if (m_ainSS != null)
        return m_ainSS.IsRunning;

      return false;
    }

    public void AnalogStart()
    {
      try
      {
        m_ainSS.Start();
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void AnalogStop()
    {
      try
      {
        m_ainSS.Stop();
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }


    public void PutDoutValue(int value)
    {
      try
      {
        m_doutSS.SetSingleValue(value);
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void PutDoutMask()
    {
      try
      {
        PutDoutValue(m_outMask);
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public void SetDoutMaskBit(int whichBit, bool value)
    {
      if (value)
        m_outMask |= (1 << whichBit);
      else
        m_outMask &= ~(1 << whichBit);
    }

    public void ResetDoutMask()
    {
      m_outMask = 0;
    }

    public void GetDinValue(out int value)
    {
      try
      {
        value = m_dinSS.GetSingleValue();
      }
      catch (Exception ex)
      {
        throw ex;
      }
    }

    public bool IsDinBitSet(int whichBit)
    {
      bool result = false;
      int mask = (1 << whichBit);

      try
      {
        int maskValue = m_dinSS.GetSingleValue() & mask;
        result = (maskValue > 0) ? true : false;
      }
      catch (Exception ex)
      {
        throw ex;
      }

      return result;
    }

    public void Dispose()
    {
      if (m_ainSS != null)
        m_ainSS.Dispose();

      if (m_dinSS != null)
        m_dinSS.Dispose();

      if (m_doutSS != null)
        m_doutSS.Dispose();

      if (m_device != null)
        m_device.Dispose();
    }

    private void HandleQueueDone(object sender, GeneralEventArgs eventData)
    {
      m_ainSS.Stop();
      Log(String.Format("Queue Done received on {0}",
          eventData.Subsystem));
    }

    private void HandleQueueStopped(object sender, GeneralEventArgs eventData)
    {
      Log(String.Format("Queue Stopped received on subsystem {0}",
          eventData.Subsystem));
    }

    private void HandleBufferDone(object sender, BufferDoneEventArgs bufferDoneData)
    {
      OlBuffer olBuffer = bufferDoneData.OlBuffer;

      if (olBuffer.ValidSamples > 0)
      {
        ++m_buffersComplete;

        // Get the data as voltages
        double[] buf = olBuffer.GetDataAsVolts();

        doneSignalHandler(ref buf);

        m_ainSS.BufferQueue.QueueBuffer(olBuffer);

        Log(String.Format("{0} Buffer Complete.", m_buffersComplete));

      }
    }

    public void HandleDriverRunTimeErrorEvent(object sender, DriverRunTimeErrorEventArgs eventData)
    {
      throw new Exception(String.Format("Error: {0} Occured on subsystem {1}",
          eventData.Message, eventData.Subsystem, eventData.Subsystem.Element));
    }

    ~DTControl()
    {
      Dispose();
    }
  }
}
