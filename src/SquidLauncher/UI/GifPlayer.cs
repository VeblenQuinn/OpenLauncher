using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SquidLauncher.Core;

namespace SquidLauncher.UI
{
    /// <summary>
    /// 自带 GIF 播放器。
    ///
    /// 为什么不用 WPF 的 <c>Image</c> 直接播 GIF：WPF 的内置播放对**局部帧优化**过的 GIF 不可靠。
    /// 实测 Squid.gif（97 帧）在 WPF 里画面完全不动（400ms 采样 6 次，像素完全一致），
    /// 因为它的第 0 帧是整幅 300×311，之后 96 帧都只是几十~几百像素的小矩形增量。
    ///
    /// 这里按 GIF89a 的语义自己合成：
    ///   · 每帧按 /imgdesc 的 Left/Top 贴到累积画布的对应位置，alpha=0 的像素视为透明不覆盖；
    ///   · 显示后按 /grctlext/Disposal 决定要不要把该矩形清空（2 = 恢复背景，1 = 保留）；
    ///   · 每帧的等待时间取 /grctlext/Delay（单位 1/100 秒）。
    ///
    /// 播放由 <see cref="CompositionTarget.Rendering"/> 驱动而不是 DispatcherTimer：
    /// 主窗口构造期间 UI 线程会被占住一秒多，任何优先级的计时器都会停摆；Rendering 恢复后按
    /// 「墙上时钟」跳到该显示的那一帧，不会补播积压帧。
    ///
    /// 解码与格式转换放在后台线程（97 帧约 0.4 秒），界面线程只做内存合成，每帧 1~3ms。
    /// </summary>
    public sealed class GifPlayer : IDisposable
    {
        sealed class Frm
        {
            public byte[] Px;      // Bgra32，仅本帧矩形区域
            public int L, T, W, H;
            public int DelayMs;
            public int Disposal;
        }

        readonly Image _target;
        readonly List<Frm> _frames = new List<Frm>();
        readonly byte[] _back;                 // 累积画布（Bgra32）
        readonly int _w, _h;
        readonly Stopwatch _clock = new Stopwatch();
        WriteableBitmap _wbm;
        double _nextDueMs;
        int _idx, _prevDisp, _prevL, _prevT, _prevW, _prevH;
        bool _disposed;

        GifPlayer(Image target, int w, int h, List<Frm> frames)
        {
            _target = target;
            _w = w; _h = h;
            _frames.AddRange(frames);
            _back = new byte[w * h * 4];
            _prevDisp = 0;
        }

        /// <summary>
        /// 开始播放。解码在后台线程做，解码完自动接到 <paramref name="target"/> 上；失败只记日志不抛。
        /// 播放器实例通过 <paramref name="onReady"/> 交回调用方，窗口关闭时调 Dispose 停下计时器。
        /// </summary>
        public static void Play(Image target, string packUri, Action<GifPlayer> onReady = null)
        {
            Task.Run(() =>
            {
                try
                {
                    var dec = new GifBitmapDecoder(new Uri(packUri),
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (dec.Frames.Count == 0) return;

                    int w = dec.Frames[0].PixelWidth;
                    int h = dec.Frames[0].PixelHeight;
                    var frames = new List<Frm>(dec.Frames.Count);

                    foreach (var f in dec.Frames)
                    {
                        var conv = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                        int fw = conv.PixelWidth, fh = conv.PixelHeight;
                        var px = new byte[fw * fh * 4];
                        conv.CopyPixels(px, fw * 4, 0);

                        int delay = QueryInt(f, "/grctlext/Delay", 10);
                        if (delay <= 0) delay = 10;          // 0 的 GIF 到处都是，按 100ms 处理
                        int ms = delay * 10;
                        if (ms < 20) ms = 20;                // 快过 50fps 没意义，只会拖慢界面

                        frames.Add(new Frm
                        {
                            Px = px,
                            L = QueryInt(f, "/imgdesc/Left", 0),
                            T = QueryInt(f, "/imgdesc/Top", 0),
                            W = fw,
                            H = fh,
                            DelayMs = ms,
                            Disposal = QueryInt(f, "/grctlext/Disposal", 1),
                        });
                    }

                    var dispatcher = target.Dispatcher;
                    if (dispatcher == null) return;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 解码期间窗口可能已经关掉了，别再去碰它
                        if (!target.IsVisible) return;
                        var p = new GifPlayer(target, w, h, frames);
                        p.Start();
                        onReady?.Invoke(p);
                        Log.Info($"[闪屏] GIF 就绪：{frames.Count} 帧 {w}×{h}");
                    }));
                }
                catch (Exception ex)
                {
                    Log.Warn("GIF 解码失败：" + ex.Message);
                }
            });
        }

        static int QueryInt(BitmapFrame f, string query, int fallback)
        {
            try
            {
                if (f.Metadata is BitmapMetadata md)
                {
                    var v = md.GetQuery(query);
                    if (v != null) return Convert.ToInt32(v);
                }
            }
            catch { }
            return fallback;
        }

        void Start()
        {
            _wbm = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
            _target.Source = _wbm;
            _idx = 0;
            Draw();                                       // 先画第一帧，别让闪屏空着
            _nextDueMs = _frames[0].DelayMs;
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }

        void OnRendering(object sender, EventArgs e)
        {
            if (_disposed) return;
            if (_clock.Elapsed.TotalMilliseconds < _nextDueMs) return;
            Step();
        }

        /// <summary>走到下一帧。掉帧不补播，直接按当前时间重新排期。</summary>
        void Step()
        {
            if (_frames.Count == 0) return;

            _idx++;
            if (_idx >= _frames.Count)
            {
                // 循环：清空画布重来（第 0 帧是整幅，会自动铺满）
                Array.Clear(_back, 0, _back.Length);
                _idx = 0;
                _prevDisp = 0;
            }

            Draw();
            _nextDueMs = _clock.Elapsed.TotalMilliseconds + _frames[_idx].DelayMs;
        }

        /// <summary>把当前帧合成进累积画布并推到界面</summary>
        void Draw()
        {
            var f = _frames[_idx];

            // 1) 上一帧的处置方式
            if (_prevDisp == 2) ClearRect(_prevL, _prevT, _prevW, _prevH);

            // 2) 贴当前帧（透明像素不覆盖）
            for (int y = 0; y < f.H; y++)
            {
                int ty = f.T + y;
                if (ty < 0 || ty >= _h) continue;
                int src = y * f.W * 4;
                int dst = (ty * _w + f.L) * 4;
                for (int x = 0; x < f.W; x++)
                {
                    int tx = f.L + x;
                    if (tx < 0 || tx >= _w) { src += 4; dst += 4; continue; }
                    if (f.Px[src + 3] != 0)
                    {
                        _back[dst] = f.Px[src];
                        _back[dst + 1] = f.Px[src + 1];
                        _back[dst + 2] = f.Px[src + 2];
                        _back[dst + 3] = f.Px[src + 3];
                    }
                    src += 4; dst += 4;
                }
            }

            _prevDisp = f.Disposal; _prevL = f.L; _prevT = f.T; _prevW = f.W; _prevH = f.H;

            // 3) 推到界面
            try { _wbm.WritePixels(new System.Windows.Int32Rect(0, 0, _w, _h), _back, _w * 4, 0); }
            catch (Exception ex) { Log.Warn("GIF 合成写屏失败：" + ex.Message); Dispose(); }
        }

        void ClearRect(int left, int top, int w, int h)
        {
            for (int y = top; y < top + h; y++)
            {
                if (y < 0 || y >= _h) continue;
                int rowBase = y * _w;
                for (int x = left; x < left + w; x++)
                {
                    if (x < 0 || x >= _w) continue;
                    int i = (rowBase + x) * 4;
                    _back[i] = 0; _back[i + 1] = 0; _back[i + 2] = 0; _back[i + 3] = 0;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { CompositionTarget.Rendering -= OnRendering; } catch { }
            if (_target != null) _target.Source = null;
        }
    }
}
