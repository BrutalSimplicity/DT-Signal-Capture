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
using System.Threading.Tasks;
using System.Net.Sockets;
using DTServer;
using System.IO;


namespace Lab5DotNet
{
  public partial class frmDTServer : Form
  {
    const string IPAddress = "192.168.1.107";
    //const string IPAddress = "127.0.0.1";
    const int Port = 26500;

    Server dtctrlServer;
    Thread serverThread;

    StreamWriter sw;

    public frmDTServer()
    {
      InitializeComponent();
      this.FormBorderStyle = FormBorderStyle.Fixed3D;
      sw = new StreamWriter("server.log");
    }

    private void btnStartStop_Click(object sender, EventArgs e)
    {
      //start connection
      dtctrlServer = new Server(26111);
      dtctrlServer.Log += WriteToFileLog;
      dtctrlServer.Log += WriteToUILog;
      serverThread = new Thread(dtctrlServer.Run);
      serverThread.Start();
      btnStartStop.Enabled = false;
    }

    private void WriteToUILog(string s)
    {
      if (tbLog.InvokeRequired)
      {
        Server.logger log = WriteToUILog;
        tbLog.Invoke(log, new object[] { s });
      }
      else
        tbLog.AppendText(String.Format("[{0} {1}]  {2}\r\n",
            DateTime.Now.ToShortDateString(), DateTime.Now.ToLongTimeString(), s));
    }

    private void WriteToFileLog(string s)
    {
      sw.WriteLine(String.Format("[{0} {1}]  {2}",
              DateTime.Now.ToShortDateString(), DateTime.Now.ToLongTimeString(), s));
    }

    private void Form1_FormClosed(object sender, FormClosedEventArgs e)
    {
      if (dtctrlServer != null)
        dtctrlServer.Stop();
      sw.Close();
    }

    private void dT9816TestToolStripMenuItem_Click(object sender, EventArgs e)
    {
      frmTest tester = new frmTest();
      tester.FormBorderStyle = FormBorderStyle.FixedDialog;
      tester.ShowDialog();
    }
  }
}
