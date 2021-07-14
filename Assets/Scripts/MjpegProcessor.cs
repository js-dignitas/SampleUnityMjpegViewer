using UnityEngine;
using System.Collections;
using System;
using System.Text;
using System.Net;
using System.IO;
using System.Threading;

public class MjpegProcessor {

    // 2 byte header for JPEG images
    private readonly byte[] JpegHeader = new byte[] { 0xff, 0xd8 };
    // pull down 1024 bytes at a time
    private int _chunkSize = 1024*4;
    private byte[] readBuf;
    // used to cancel reading the stream
    private bool _streamActive;
    // current encoded JPEG image
    public byte[] CurrentFrame { get; private set; }
    // WPF, Silverlight
    //public BitmapImage BitmapImage { get; set; }
    // used to marshal back to UI thread
    private SynchronizationContext _context;
    public byte[] latestFrame = null;

    // event to get the buffer above handed to you
    public event EventHandler<FrameReadyEventArgs> FrameReady;
    public event EventHandler<ErrorEventArgs> Error;

    public MjpegProcessor(int chunkSize = 4 * 1024)
    {
        _context = SynchronizationContext.Current;
        _chunkSize = chunkSize;
        readBuf = new byte[_chunkSize];
    }


    Uri uri;
    string username;
    string password;
    public void ParseStream(Uri uri)
    {
        ParseStream(uri, null, null);
    }

    HttpWebRequest request;
    public void ParseStream(Uri uri, string username, string password)
    {
        this.uri = uri;
        this.username = username;
        this.password = password;
        Debug.Log("Parsing Stream " + uri.ToString());
        request = (HttpWebRequest)WebRequest.Create(uri);
        if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            request.Credentials = new NetworkCredential(username, password);
        // asynchronously get a response
        request.BeginGetResponse(OnGetResponse, request);
    }

    public void Refresh()
    {
        _streamActive = false;
        ParseStream(uri, username, password);
    }
    public void StopStream()
    {
        UnityEngine.Debug.Log("Stream stopped");
        _streamActive = false;
    }
    public static int FindBytes(byte[] buff, int buffLength, byte[] search)
    {
        // enumerate the buffer but don't overstep the bounds
        for (int start = 0; start < buffLength - search.Length; start++)
        {
            // we found the first character
            if (buff[start] == search[0])
            {
                int next;

                // traverse the rest of the bytes
                for (next = 1; next < search.Length; next++)
                {
                    // if we don't match, bail
                    if (buff[start + next] != search[next])
                        break;
                }

                if (next == search.Length)
                    return start;
            }
        }
        // not found
        return -1;
    }
    public static int FindBytesInReverse(byte[] buff, int buffLength, byte[] search)
    {
        // enumerate the buffer but don't overstep the bounds
        for (int start = buffLength - search.Length - 1; start > 0; start--)
        {
            // we found the first character
            if (buff[start] == search[0])
            {
                int next;

                // traverse the rest of the bytes
                for (next = 1; next < search.Length; next++)
                {
                    // if we don't match, bail
                    if (buff[start + next] != search[next])
                        break;
                }

                if (next == search.Length)
                    return start;
            }
        }
        // not found
        return -1;
    }

    byte[] imageBuffer = new byte[1024 * 1024];

    int count = 0;
    private void OnGetResponse(IAsyncResult asyncResult)
    {
        count = 0;
        Debug.Log("OnGetResponse");

        Debug.Log("Starting request");
        // get the response
        HttpWebRequest req = (HttpWebRequest)asyncResult.AsyncState;

        try
        {
            UnityEngine.Debug.Log(System.Threading.Thread.CurrentThread.Name + ": OnGetResponse try entered.");
            HttpWebResponse resp = (HttpWebResponse)req.EndGetResponse(asyncResult);
            UnityEngine.Debug.Log("response received");
            // find our magic boundary value
            string contentType = resp.Headers["Content-Type"];
            if (!string.IsNullOrEmpty(contentType) && !contentType.Contains("="))
            {
                Debug.Log("MJPEG Exception thrown");
                throw new Exception("Invalid content-type header.  The camera is likely not returning a proper MJPEG stream.");
            }

            string boundary = resp.Headers["Content-Type"].Split('=')[1].Replace("\"", "");
            byte[] boundaryBytes = Encoding.UTF8.GetBytes(boundary.StartsWith("--") ? boundary : "--" + boundary);

            Stream s = resp.GetResponseStream();
            BinaryReader br = new BinaryReader(s);

            _streamActive = true;
            int bufLength = br.Read(readBuf, 0, readBuf.Length);

            while (_streamActive)
            {
                // find the JPEG header
                int imageStart = FindBytes(readBuf, bufLength, JpegHeader);// readBuf.Find(JpegHeader);

                if (imageStart != -1)
                {
                    // copy the start of the JPEG image to the imageBuffer
                    int size = bufLength - imageStart;
                    Array.Copy(readBuf, imageStart, imageBuffer, 0, size);

                    while (true)
                    {
                        bufLength = br.Read(readBuf, 0, readBuf.Length);

                        // Find the end of the jpeg
                        int imageEnd = FindBytes(readBuf, bufLength, boundaryBytes);
                        if (imageEnd != -1)
                        {
                            // copy the remainder of the JPEG to the imageBuffer
                            Array.Copy(readBuf, 0, imageBuffer, size, imageEnd);
                            size += imageEnd;

                            // Copy the latest frame into `CurrentFrame`
                            byte[] frame = new byte[size];
                            Array.Copy(imageBuffer, 0, frame, 0, size);
                            CurrentFrame = frame;

                            // tell whoever's listening that we have a frame to draw
                            if (FrameReady != null)
                                FrameReady(this, new FrameReadyEventArgs());
                            count++;
                            if (count > 10)
                            {
                                resp.Close();
                                Refresh();
                                break;
                            }
                            // copy the leftover data to the start
                            Array.Copy(readBuf, imageEnd, readBuf, 0, bufLength - imageEnd);

                            // fill the remainder of the readBufer with new data and start over
                            byte[] temp = br.ReadBytes(imageEnd);

                            Array.Copy(temp, 0, readBuf, bufLength - imageEnd, temp.Length);
                            break;
                        }

                        // copy all of the data to the imageBuffer
                        Array.Copy(readBuf, 0, imageBuffer, size, bufLength);
                        size += bufLength;

                        if (!_streamActive)
                        {
                            resp.Close();
                            break;
                        }
                    }
                }
            }
            resp.Close();
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogException(ex);
            if (Error != null)
                _context.Post(delegate { Error(this, new ErrorEventArgs() { Message = ex.Message }); }, null);

            return;
        }
    }
}

public class FrameReadyEventArgs : EventArgs
{
  
}

public sealed class ErrorEventArgs : EventArgs

{
    public string Message { get; set; }
    public int ErrorCode { get; set; }
}