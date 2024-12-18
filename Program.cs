using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Fleck;
using SkiaSharp;

class ZKFingerSocketServer
{
    [DllImport("libzkfp.so", EntryPoint = "ZKFPM_Init", CallingConvention = CallingConvention.StdCall)]
    public static extern int ZKFPM_Init();

    [DllImport("libzkfp.so", EntryPoint = "ZKFPM_Terminate", CallingConvention = CallingConvention.StdCall)]
    public static extern int ZKFPM_Terminate();

    [DllImport("libzkfp.so", EntryPoint = "ZKFPM_GetDeviceCount", CallingConvention = CallingConvention.StdCall)]
    public static extern int ZKFPM_GetDeviceCount();

    [DllImport("libzkfp.so", EntryPoint = "ZKFPM_OpenDevice", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr ZKFPM_OpenDevice(int index);

    [DllImport("libzkfp.so", EntryPoint = "ZKFPM_CloseDevice", CallingConvention = CallingConvention.StdCall)]
    public static extern int ZKFPM_CloseDevice(IntPtr hDevice);

    [DllImport("libzkfp.so", EntryPoint = "ZKFPM_AcquireFingerprint", CallingConvention = CallingConvention.StdCall)]
    public static extern int ZKFPM_AcquireFingerprint(
        IntPtr hDevice,
        byte[] fpImage,
        uint cbFPImage,
        byte[] fpTemplate,
        ref uint cbTemplate
    );

    private IntPtr hDevice = IntPtr.Zero;
    private byte[] imgbuf;
    private int imgWidth = 300; // Adjust based on your device
    private int imgHeight = 400; // Adjust based on your device
    private bool isRunning = false;
    private WebSocketServer socketServer;
    private List<IWebSocketConnection> clients = new List<IWebSocketConnection>();

    public void Start()
    {
        // Initialize SDK
        int initResult = ZKFPM_Init();
        if (initResult != 0)
        {
            Console.WriteLine($"Failed to initialize SDK. Error code: {initResult}");
            return;
        }

        int deviceCount = ZKFPM_GetDeviceCount();
        if (deviceCount <= 0)
        {
            Console.WriteLine("No fingerprint devices found.");
            ZKFPM_Terminate();
            return;
        }

        hDevice = ZKFPM_OpenDevice(0);
        if (hDevice == IntPtr.Zero)
        {
            Console.WriteLine("Failed to open device.");
            ZKFPM_Terminate();
            return;
        }

        Console.WriteLine("Device opened successfully.");
        imgbuf = new byte[imgWidth * imgHeight];

        // Start WebSocket server
        StartWebSocketServer();

        // Start fingerprint capture thread
        isRunning = true;
        Thread captureThread = new Thread(CaptureFingerprint);
        captureThread.Start();
    }

    public void Stop()
    {
        isRunning = false;

        if (hDevice != IntPtr.Zero)
        {
            ZKFPM_CloseDevice(hDevice);
        }

        ZKFPM_Terminate();
        Console.WriteLine("SDK terminated.");
    }

    private void StartWebSocketServer()
    {
        socketServer = new WebSocketServer("ws://0.0.0.0:5000");
        socketServer.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                Console.WriteLine("Client connected.");
                lock (clients)
                {
                    clients.Add(socket);
                }
            };

            socket.OnClose = () =>
            {
                Console.WriteLine("Client disconnected.");
                lock (clients)
                {
                    clients.Remove(socket);
                }
            };

            socket.OnMessage = message =>
            {
                Console.WriteLine($"Message received from client: {message}");
            };
        });

        Console.WriteLine("WebSocket server started on ws://0.0.0.0:5000");
    }

    private void CaptureFingerprint()
    {
        while (isRunning)
        {
            byte[] fpTemplate = new byte[2048];
            uint cbFPImage = (uint)imgbuf.Length;
            uint cbTemplate = (uint)fpTemplate.Length;

            int captureResult = ZKFPM_AcquireFingerprint(hDevice, imgbuf, cbFPImage, fpTemplate, ref cbTemplate);

            if (captureResult == 0)
            {
                Console.WriteLine("Fingerprint captured successfully.");

                // Convert raw fingerprint image to PNG format
                byte[] pngImage = ConvertRawToPng(imgbuf, imgWidth, imgHeight);
                string base64Image = Convert.ToBase64String(pngImage);

                // Broadcast Base64 fingerprint image
                BroadcastToClients(base64Image);
            }
            else
            {
                Console.WriteLine($"Failed to capture fingerprint. Error code: {captureResult}");
            }

            Thread.Sleep(100); // Adjust latency as needed
        }
    }

    private void BroadcastToClients(string data)
    {
        string base64Image = $"data:image/png;base64,{data}";

        lock (clients)
        {
            foreach (var client in clients)
            {
                if (client.IsAvailable)
                {
                    client.Send(base64Image);
                }
            }
        }
    }

    private byte[] ConvertRawToPng(byte[] rawImage, int width, int height)
    {
        using (var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8)))
        {
            // Copy raw image data into bitmap
            Marshal.Copy(rawImage, 0, bitmap.GetPixels(), rawImage.Length);

            // Encode bitmap to PNG format
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            {
                return data.ToArray();
            }
        }
    }

    static void Main(string[] args)
    {
        var server = new ZKFingerSocketServer();
        server.Start();

        Console.WriteLine("Press Enter to stop...");
        Console.ReadLine();

        server.Stop();
    }
}
