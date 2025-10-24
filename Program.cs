using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using MathNet.Numerics.IntegralTransforms;

class Program
{
    const int FFT_SIZE = 1024;                 // 1024點FFT ≈ 21ms@48k
    const int HOP = FFT_SIZE / 2;              // 50% overlap
    const int BANDS = 32;                      // 等化器柱數
    const double F_MIN = 20, F_MAX = 20000;    // 頻帶範圍
    const int UDP_PORT = 31337;

    static float[] hann = Enumerable.Range(0, FFT_SIZE)
        .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FFT_SIZE - 1))))
        .ToArray();

    static float[] ring = new float[FFT_SIZE * 4];
    static int writePos = 0, available = 0;

    static double[] prev = new double[BANDS];  // 平滑狀態
    static (int start, int end)[] bandBins;

    static void Main()
    {
        using var udp = new UdpClient() { EnableBroadcast = true };
        var endpoint = new IPEndPoint(IPAddress.Broadcast, UDP_PORT);

        using var cap = new WasapiLoopbackCapture(); // 預設播放端
        var sr = cap.WaveFormat.SampleRate;
        var ch = cap.WaveFormat.Channels;
        Console.WriteLine($"Loopback: {sr} Hz, {ch} ch, {cap.WaveFormat.Encoding}");

        bandBins = BuildLogBands(sr, FFT_SIZE, BANDS, F_MIN, F_MAX);

        cap.DataAvailable += (s, e) =>
        {
            try
            {
                // 直接用 WaveBuffer 以 float 取樣為單位來讀，避免手算位移
                var wb = new WaveBuffer(e.Buffer);
                int bytesPerSample = 4; // WasapiLoopbackCapture 輸出 32-bit float
                int ch = cap.WaveFormat.Channels;
                int totalSamples = e.BytesRecorded / bytesPerSample; // float 數量
                int frames = totalSamples / ch;                      // 幾個「多聲道 frame」

                for (int n = 0; n < frames; n++)
                {
                    // 轉 mono（多聲道平均）
                    float sum = 0f;
                    int baseIdx = n * ch;
                    for (int c = 0; c < ch; c++)
                        sum += wb.FloatBuffer[baseIdx + c];
                    float mono = sum / ch;

                    ring[writePos] = mono;
                    writePos = (writePos + 1) % ring.Length;
                    if (available < ring.Length) available++;        // 防止 available 無限制飆高
                }

                // 只要足夠就處理（每次前進 HOP）
                while (available >= FFT_SIZE)
                {
                    var block = new System.Numerics.Complex[FFT_SIZE];
                    for (int i = 0; i < FFT_SIZE; i++)
                    {
                        int rp = (writePos - available + i + ring.Length) % ring.Length;
                        block[i] = new System.Numerics.Complex(ring[rp] * hann[i], 0.0);
                    }

                    Fourier.Forward(block, FourierOptions.Matlab);

                    var levels = new double[BANDS];
                    for (int b = 0; b < BANDS; b++)
                    {
                        double sumMag = 0; int count = 0;
                        var (bs, be) = bandBins[b];
                        bs = Math.Max(bs, 1);
                        be = Math.Min(be, FFT_SIZE / 2 - 1);

                        for (int k = bs; k <= be; k++)
                        {
                            double mag = block[k].Magnitude;
                            sumMag += mag; count++;
                        }
                        double val = (count > 0 ? sumMag / count : 0);
                        double db = 20.0 * Math.Log10(val + 1e-9);
                        double norm = Math.Clamp((db + 80.0) / 80.0, 0, 1);
                        double comp = Math.Pow(norm, 0.6);

                        double attack = 0.35, release = 0.08;
                        double alpha = (comp > prev[b]) ? attack : release;
                        prev[b] = prev[b] * (1 - alpha) + comp * alpha;

                        levels[b] = Math.Round(prev[b] * 255.0);
                    }

                    var payload = new byte[3 + BANDS];
                    payload[0] = (byte)'E';
                    payload[1] = (byte)'Q';
                    payload[2] = 1;
                    for (int i = 0; i < BANDS; i++)
                        payload[3 + i] = (byte)levels[i];

                    udp.Send(payload, payload.Length, endpoint);

                    available -= HOP;
                }
            }
            catch (Exception ex)
            {
                // 把錯誤印出但不中斷錄音；真的要停再 StopRecording()
                Console.WriteLine($"[DataAvailable] {ex.GetType().Name}: {ex.Message}");
            }
        };


        cap.RecordingStopped += (s, e) => Console.WriteLine($"Stopped: {e.Exception?.Message}");
        cap.StartRecording();
        Console.WriteLine($"Streaming {BANDS} bands via UDP broadcast :{UDP_PORT}. Press ENTER to stop.");
        Console.ReadLine();
        cap.StopRecording();
    }

    static (int, int)[] BuildLogBands(int sr, int fft, int bands, double fmin, double fmax)
    {
        var edges = new double[bands + 1];
        double lnMin = Math.Log(fmin), lnMax = Math.Log(fmax);
        for (int i = 0; i <= bands; i++)
            edges[i] = Math.Exp(lnMin + (lnMax - lnMin) * i / bands);

        double hzPerBin = (double)sr / fft;
        var map = new (int, int)[bands];
        for (int b = 0; b < bands; b++)
        {
            int start = (int)Math.Floor(edges[b] / hzPerBin);
            int end   = (int)Math.Ceiling(edges[b + 1] / hzPerBin);
            map[b] = (start, Math.Max(start, end));
        }
        return map;
    }
}
