using UnityEngine;
using System;
using System.IO;

#if NET_4_6
using System.Drawing;
using System.Drawing.Imaging;
using Graphics = System.Drawing.Graphics;
#endif

using System.Runtime.InteropServices;
using System.Threading.Tasks;

/// <summary>
/// A Unity3D Script to dipsplay Mjpeg streams. Apply this script to the mesh that you want to use to view the Mjpeg stream. 
/// </summary>
public class MjpegTexture : MonoBehaviour
{
    /// <param name="streamAddress">
    /// Set this to be the network address of the mjpg stream. 
    /// Example: "http://extcam-16.se.axis.com/mjpg/video.mjpg"
    /// </param>
    [Tooltip("Set this to be the network address of the mjpg stream. ")]
    [SerializeField]
    private string streamAddress;
    
    private int index = 0;
#if NET_4_6
    private System.Drawing.Bitmap bmp = null;
#endif
    public string StreamAddress
    {
        get => streamAddress;
        set
        {
            if (streamAddress != value)
            {
                streamAddress = value;
                if (playing)
                {
                    Stop();
                    Play();
                }
            }
        }
    }
    /// <summary>
    /// Show fps (OnGUI).
    /// </summary>
    [Tooltip("Show fps (OnGUI).")]
    public bool showFps = true;

    /// <summary>
    /// Chunk size for stream processor in kilobytes.
    /// </summary>
    [Tooltip("Chunk size for stream processor in kilobytes.")]
    public int chunkSize = 4;

    public Material material;
    bool materialIsCopy = false;

    Texture2D tex;

    const int initWidth = 2;
    const int initHeight = 2;

    bool updateFrame = false;

    MjpegProcessor mjpeg;

    float deltaTime = 0.0f;
    float mjpegDeltaTime = 0.0f;

    Jpeg.Jpeg jpeg = new Jpeg.Jpeg();

    public bool autoPlay = true;

    private bool playing = false;
    
    bool downloading = false;

    byte[] imgBytes;

    bool decoding = false;

    private int resetAfterThisManyFrames = 30;
    public int ResetAfterThisManyFrames
    {
        get { return resetAfterThisManyFrames; }
        set
        {
            this.resetAfterThisManyFrames = value;
            if (mjpeg != null)
            {
                mjpeg.ResetAfterThisManyFrames = value;
            }
        }
    }
    // Update is called once per frame
    public void Start()
    {
        if (material == null)
        {
            material = GetComponent<Renderer>().material;
        }
        else
        {
            material = new Material(material);
            GetComponent<Renderer>().sharedMaterial = material;
        }
        if (autoPlay)
        {
            Play();
        }
    }

    private void Init()
    {
        if (mjpeg == null)
        {
            mjpeg = new MjpegProcessor(chunkSize * 1024);
            mjpeg.FrameReady += OnMjpegFrameReady;
            mjpeg.Error += OnMjpegError;
            mjpeg.ResetAfterThisManyFrames = ResetAfterThisManyFrames;
        }
    }
    public void Play()
    {
        Init();
        if (!playing)
        {
            playing = true;
            Uri mjpegAddress = new Uri(streamAddress);
            mjpeg.ParseStream(mjpegAddress);
        }
    }

    public void Stop()
    {
        if (playing)
        {
            playing = false;
            if (mjpeg != null)
            {
                mjpeg.StopStream();
                mjpeg = null;
            }
        }
    }
#if NET_4_6
    static byte[] BitmapToByteArray(Bitmap bitmap, byte[] reuse)
    {

        BitmapData bmpdata = null;

        try
        {
            bmpdata = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
            int numbytes = bmpdata.Stride * bitmap.Height;
            byte[] bytedata = reuse;
            if (bytedata == null || bytedata.Length != numbytes)
            {
                bytedata = new byte[numbytes];
            }

            IntPtr ptr = bmpdata.Scan0;

            Marshal.Copy(ptr, bytedata, 0, numbytes);

            return bytedata;
        }
        finally
        {
            if (bmpdata != null)
                bitmap.UnlockBits(bmpdata);
        }

    }
    private async Task LoadJpgData(byte[] bytes)
    {
        int height = 1;
        int width = 1;
        
        await Task.Run(() =>
        {
            // I tested with System.Threading.Thread.Sleep(1000) here to make 
            // sure that even if this goes slow, it does not back up a bunch of mjpeg 
            // frames in a buffer.  The frames stay up to date
            using (MemoryStream inStream = new MemoryStream(bytes))
            {
                var image = System.Drawing.Image.FromStream(inStream, true, false);
                height = image.Size.Height;
                width = image.Size.Width;

                // Reusing the bitmap if possible
                if (bmp == null || bmp.Size.Height != height || bmp.Size.Width != width)
                {
                    bmp = new System.Drawing.Bitmap(image);
                }
                else
                {
                    // Draws into bmp
                    using (Graphics graphics = Graphics.FromImage(bmp))
                    {
                        graphics.DrawImage(image, 0, 0, width, height);
                    }
                }

                // Reusing imgBytes if possible
                imgBytes = BitmapToByteArray(bmp, imgBytes);
            }
        });

        // Could have been destroyed during the Task
        if (material == null)
        {
            return;
        }
        
        // Reusing Texture2D if possible
        if (tex == null || tex.width != width || tex.height != height)
        {
            tex = new Texture2D(width, height, TextureFormat.BGRA32, false);
        }

        // Loading the bytes from the bitmap
        tex.LoadRawTextureData(imgBytes);

        tex.Apply();

        material.mainTexture = tex;
    }
#else 
    private async Task LoadJpgData(byte[] bytes)
    {
        // .NET Standard 2.0 Implementation
        decoding = true;
        await Task.Run(() =>
        {
            jpeg.ParseData(bytes);
            imgBytes = jpeg.DecodeScan(imgBytes, true);
        });

        decoding = false;
        // Could have been destroyed during the Task
        if (material == null)
        {
            return;
        }
        if (tex == null)
        {
            tex = new Texture2D(jpeg.Width, jpeg.Height, TextureFormat.RGB24, false);
        }
        tex.LoadRawTextureData(imgBytes);
        tex.Apply();

        material.mainTexture = tex;
    }
#endif
    private void OnMjpegFrameReady(object sender, FrameReadyEventArgs e)
    {
        updateFrame = true;
    }
    void OnMjpegError(object sender, ErrorEventArgs e)
    {
        Debug.Log("Error received while reading the MJPEG.");
    }

    async void Update()
    {
        if (!playing)
            return;
        
        deltaTime += Time.deltaTime;

        if (updateFrame)
        {
            if (!decoding)
            {
                decoding = true;
                if (mjpeg.CurrentFrame != null)
                {
                    await LoadJpgData(mjpeg.CurrentFrame);
                }

                updateFrame = false;

                if (material == null)
                {
                    return;
                }

                mjpegDeltaTime += (deltaTime - mjpegDeltaTime) * 0.2f;

                deltaTime = 0.0f;
                decoding = false;
            }
        }
    }

    void DrawFps()
    {
        int w = Screen.width, h = Screen.height;

        GUIStyle style = new GUIStyle();

        Rect rect = new Rect(20, 20 + (h * 4 / 100 + 10), w, h * 2 / 100);
        style.alignment = TextAnchor.UpperLeft;
        style.fontSize = h * 4 / 100;
        style.normal.textColor = new UnityEngine.Color(255, 255, 255, 255);
        float msec = mjpegDeltaTime * 1000.0f;
        float fps = 1.0f / mjpegDeltaTime;
        string text = string.Format("MJPEG: {0:0.0} ms ({1:0.} fps)", msec, fps);
        GUI.Label(rect, text, style);
    }

    void OnGUI()
    {
        if (showFps) DrawFps();
    }

    void OnDestroy()
    {
        Stop();
        Destroy(material);
    }
}