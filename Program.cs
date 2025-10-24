using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using MathNet.Numerics.IntegralTransforms;

class Program
{
    // ===== 你可視需求微調的參數 =====
    const int FFT_SIZE = 1024;                 // 1024 點 FFT（~21ms@48k）
    const int HOP = FFT_SIZE / 2;              // 50% overlap
    const int BANDS = 32;                      // 頻帶數
    const double F_MIN = 80, F_MAX = 20000;    // 降低超低頻影響：80Hz 起跳
    const int UDP_PORT = 31337;

    // dBFS 映射範圍（把 band 的 dBFS 映到 0..1）
    const double MIN_DB = -60.0;
    const double MAX_DB = 0.0;

    // Hann 視窗的 coherent gain（相干增益）≈ 0.5
    const double Cg = 0.5;

    static float[] hann = Enumerable.Range(0, FFT_SIZE)
        .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FFT_SIZE - 1))))
        .ToArray();

    // 環形緩衝（存 mono）
    static float[] ring = new float[FFT_SIZE * 4];
    static int writePos = 0, available = 0;

    static double[] prev = new double[BANDS];  // 平滑狀態
    static (int start, int end)[] bandBins;

    static void Main()
    {
        using var udp = new UdpClient() { EnableBroadcast = true };
        var endpoint = new IPEndPoint(IPAddress.Broadcast, UDP_PORT);

        using var cap = new WasapiLoopbackCapture(); // 抓系統播放端
        var sr = cap.WaveFormat.SampleRate;
        var ch = cap.WaveFormat.Channels;
        Console.WriteLine($"Loopback: {sr} Hz, {ch} ch, {cap.WaveFormat.Encoding}");

        bandBins = BuildLogBands(sr, FFT_SIZE, BANDS, F_MIN, F_MAX);

        cap.DataAvailable += (s, e) =>
        {
            try
            {
                // 用 WaveBuffer 以 float 為單位讀，避免位移計算錯誤
                var wb = new WaveBuffer(e.Buffer);
                int bytesPerSample = 4; // WasapiLoopbackCapture 為 32-bit float
                int totalSamples = e.BytesRecorded / bytesPerSample;
                int frames = totalSamples / ch;

                // 多聲道 → mono（平均）
                for (int n = 0; n < frames; n++)
                {
                    float sum = 0f;
                    int baseIdx = n * ch;
                    for (int c = 0; c < ch; c++)
                        sum += wb.FloatBuffer[baseIdx + c];
                    float mono = sum / ch;

                    ring[writePos] = mono;
                    writePos = (writePos + 1) % ring.Length;
                    if (available < ring.Length) available++; // 上限保護
                }

                // 只要夠一窗就處理（每次前進 HOP）
                while (available >= FFT_SIZE)
                {
                    var block = new System.Numerics.Complex[FFT_SIZE];
                    for (int i = 0; i < FFT_SIZE; i++)
                    {
                        int rp = (writePos - available + i + ring.Length) % ring.Length;
                        block[i] = new System.Numerics.Complex(ring[rp] * hann[i], 0.0);
                    }

                    // FFT
                    Fourier.Forward(block, FourierOptions.Matlab);

                    // 單邊頻譜正規化因子：2/(N*Cg)
                    double normSpec = 2.0 / (FFT_SIZE * Cg);

                    var levels = new double[BANDS];

                    for (int b = 0; b < BANDS; b++)
                    {
                        double sumPow = 0.0; int count = 0;
                        var (bs, be) = bandBins[b];

                        // 跳過 DC 與 Nyquist：僅用 1..N/2-1
                        int kStart = Math.Max(bs, 1);
                        int kEnd   = Math.Min(be, FFT_SIZE / 2 - 1);

                        for (int k = kStart; k <= kEnd; k++)
                        {
                            double re = block[k].Real * normSpec;
                            double im = block[k].Imaginary * normSpec;
                            double pow = re * re + im * im; // 功率
                            sumPow += pow;
                            count++;
                        }

                        // 頻帶 RMS（功率平均再開根號）→ 轉成 dBFS
                        double bandRms = (count > 0) ? Math.Sqrt(sumPow / count) : 0.0;
                        double db = 20.0 * Math.Log10(bandRms + 1e-12); // dBFS

                        // 映射 dBFS 到 0..1
                        double norm = (db - MIN_DB) / (MAX_DB - MIN_DB);
                        norm = Math.Clamp(norm, 0.0, 1.0);

                        // 輕度壓縮（gamma）＋ 攻擊/釋放 平滑
                        double comp = Math.Pow(norm, 0.6);
                        double attack = 0.35, release = 0.08;
                        double alpha = (comp > prev[b]) ? attack : release;
                        prev[b] = prev[b] * (1 - alpha) + comp * alpha;

                        levels[b] = Math.Round(prev[b] * 255.0);
                    }

                    // 打包送出：'E','Q',version=1, <BANDS bytes>
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
        // 對數切分：回傳每帶對應的 FFT bin 起訖（[start, end]，含端點）
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
