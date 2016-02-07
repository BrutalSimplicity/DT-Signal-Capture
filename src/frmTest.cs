using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DT9816;

namespace Lab5DotNet
{
  public partial class frmTest : Form
  {
    DTControl driver;
    static int numBuffers = 0;
    static bool stopped = true;

    public delegate void SignalDelegate(ref double[] buf);

    public frmTest()
    {
      InitializeComponent();
    }

    private void button1_Click(object sender, EventArgs e)
    {
      try
      {
        if (stopped)
        {
          if (driver == null)
            driver = new DTControl(1000, new int[] { 0, 1 }, new DTControl.Logger(MessageLog), new DTControl.DoneSignalHandler(SignalsDone));
          driver.AnalogStart();
          button1.Text = "STOP";
          stopped = false;
        }
        else
        {
          driver.AnalogStop();
          //button1.Text = "Get";
          stopped = true;
        }
        
      }
      catch (Exception ex)
      {
        MessageBox.Show(ex.Message);
      }
    }

    private void MessageLog(string info)
    {
      if (this.InvokeRequired)
        this.Invoke(new DTControl.Logger(MessageLog), new object[] {info});
      else
        textBox5.AppendText(info + "\r\n");
    }

    private void SignalsDone(ref double[] buf)
    {
      if (this.InvokeRequired)
        this.Invoke(new SignalDelegate(SignalsDone), new object[] { buf });
      else
      {
        textBox2.Text = buf[0].ToString();
        textBox3.Text = buf[1].ToString();
        int value;
        driver.GetDinValue(out value);
        textBox1.Text = value.ToString();

        if (numBuffers < 5)
        {
          textBox5.AppendText(String.Format("Buffer Length = {0}", buf.Length));
        }
        numBuffers++;
      }
    }

    private void button2_Click(object sender, EventArgs e)
    {
      if (driver != null)
      {
        int value = int.Parse(textBox4.Text);

        driver.PutDoutValue(value);
      }
    }
  }
}
