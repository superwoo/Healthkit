using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using SkiaSharp;

using Water.Healthkit.Enums;
using Water.Healthkit.Extensions;
using Water.Toolkit.Collections;
using Water.Toolkit.Extensions;
using Water.Toolkit.Logs;
using Water.Toolkit.Models;

namespace Water.Healthkit.Drawing
{
    /// <summary>
    /// 生理电图渲染器类。
    /// </summary>
    /// <remarks>
    /// <para><b>用途</b>: 使用 SkiaSharp 绘制心电图(ECG)波形，支持实时滚动显示与离线诊断报告两种模式。</para>
    /// <para><b>支持的导联布局</b>:
    /// <list type="bullet">
    /// <item>LM_6x2 — 6导联2列（12导联分左右两列）</item>
    /// <item>LM_6x2x1 — 6导联2列 + 1条次导联（节律导联）</item>
    /// <item>LM_6x3 — 6导联3列（18导联分左中右三列）</item>
    /// <item>LM_6x3x1 — 6导联3列 + 1条次导联</item>
    /// <item>LM_3x4 — 3行4列（12导联矩阵排列，适用于医院报告）</item>
    /// <item>LM_12x1 — 12导联单列纵向排列</item>
    /// </list>
    /// </para>
    /// <para><b>坐标体系</b>:
    /// <list type="bullet">
    /// <item>X 轴: 时间轴，单位为像素，步长 <c>_xstep = Speed × Dpm / _drawfreq</c></item>
    /// <item>Y 轴: 振幅轴，单位为像素，1mV = 10mm × Dpm × Gain（标准增益 10mm/mV）</item>
    /// <item>Dpm: 每毫米像素数（Dots per mm），决定渲染精度</item>
    /// </list>
    /// </para>
    /// <para><b>扩展功能</b>: 主题配色、按导联组着色、手动标测卡尺测量、局部缩放（水平/垂直）、PDF 报告打印。</para>
    /// </remarks>
    public partial class EPGRender : IDisposable
    {
        #region 数据
        /// <summary>日志记录器，用于记录渲染过程中的异常与警告</summary>
        private IExLog _log = LogManager.GetLogger();

        /// <summary>
        /// 1 mm 网格笔刷
        /// </summary>
        private SKPaint _pen1mm;
        /// <summary>
        /// 5 mm 网格笔刷
        /// </summary>
        private SKPaint _pen5mm;
        /// <summary>
        /// 波形笔刷
        /// </summary>
        private SKPaint _penwave;
        /// <summary>
        /// 按导联分组的波形笔刷数组（仅 <see cref="LeadColorMode.ByGroup"/> 模式下非 null）。
        /// <br/>索引 0-17 对应 18 导联，索引 18 对应次导联。
        /// </summary>
        private SKPaint[] _leadPens;
        /// <summary>
        /// 标签笔刷
        /// </summary>
        private SKPaint _penheader;
        /// <summary>
        /// 导联标签字体
        /// </summary>
        private SKFont _fontheader;
        /// <summary>
        /// 网格背景笔刷
        /// </summary>
        private SKPaint _penbg;
        /// <summary>
        /// 中文字体
        /// </summary>
        private SKTypeface _zhcn = SKFontManager.Default.MatchCharacter('中');
        /// <summary>
        /// 英文字体
        /// </summary>
        private SKTypeface _usen = SKFontManager.Default.MatchCharacter('E');
        /// <summary>
        /// 画布宽度。
        /// </summary>
        private int _iwidth;
        /// <summary>
        /// 画布高度。
        /// </summary>
        private int _iheight;
        /// <summary>
        /// 为保证水平方向画出的大格（5个小格）为整数个，
        /// 进行水平方向的起始坐标校正。
        /// </summary>
        private float _xdelta = 0;
        /// <summary>
        /// 为保证垂直方向画出的大格（5个小格）为整数个，
        /// 进行垂直方向的起始坐标校正。
        /// </summary>
        private float _ydelta = 0;
        /// <summary>
        /// 擦除留白宽度。
        /// </summary>
        private float _espace => 5 * Dpm;
        /// <summary>
        /// 导联头宽度。
        /// </summary>
        private float _hwidth => 10 * Dpm;
        /// <summary>
        /// 导联模式（默认为单导联模式）。
        /// </summary>
        private LeadMode _mode = LeadMode.LM_6x2x1;
        /// <summary>
        /// 次导联索引，仅 <see cref="LeadMode.LM_6x2x1"/> 和 <see cref="LeadMode.LM_6x3x1"/> 有效。
        /// </summary>
        private int _secidx;
        /// <summary>
        /// 绘图频率，如采样频率 500Hz，每5个点画一次，那么绘图频率为 100Hz，即每秒画 50 个点。
        /// </summary>
        private int _drawfreq { get; set; } = 50;

        /// <summary>
        /// 导联的名称。
        /// </summary>
        private string[] _labels { get; set; } = new string[] {
            "I", "II", "III", "aVR", "aVL", "aVF", // 肢体导联
            "V1", "V2", "V3", "V4", "V5", "V6",   // 胸前导联
            "V7", "V8", "V9", "V3R", "V4R", "V5R" // 扩展导联
        };
        /// <summary>
        /// 左、中、右列导联 Index 轴原始坐标。
        /// </summary>
        private float[] _oxs = new float[4];
        /// <summary>
        /// Index 方向步长。
        /// </summary>
        private float _xstep;
        /// <summary>
        /// 绘制导联的 Value 轴基坐标集合。
        /// </summary>
        private float[] _ybases = new float[19];
        /// <summary>
        /// 左、中、右、主列导联 Index 轴实时坐标。
        /// </summary>
        private float[] _rtxs = new float[4];
        /// <summary>
        /// 导联 Value 轴实时心电幅值（单位：毫伏）。
        /// </summary>
        private float[] _rtys = new float[19];
        /// <summary>
        /// 左列导联擦除区域 Index 轴实时坐标修正值。
        /// </summary>
        private float _xedelta = -2;
        /// <summary>
        /// 是否已重置参数。
        /// </summary>
        private bool _isreset = false;

        /// <summary>
        /// 导联诊断时左中右、主列导联数据索引的偏移量。
        /// </summary>
        private int[] _offsets = new int[2];
        /// <summary>
        /// 导联诊断时波形路径集合。
        /// </summary>
        private List<SKPath> _paths = new List<SKPath>();
        /// <summary>
        /// 导联诊断时左中右导联最多显示数据个数
        /// </summary>
        private int[] _xmaxpoints = new int[2];
        /// <summary>
        /// 是否正在打印诊断信息
        /// </summary>
        private bool _isprinting = false;
        /// <summary>
        /// 增益因子，用于调整心电图的振幅，默认为 1.0，对应增益为 10mm/mV（标准增益）。
        /// </summary>
        private double _gain = 1.0;
        /// <summary>
        /// 手动标测卡尺起点（导联索引 + 数据索引），null 表示未设置。
        /// </summary>
        private CaliperPoint? _caliperStart;
        /// <summary>
        /// 手动标测卡尺终点（导联索引 + 数据索引），null 表示未设置。
        /// </summary>
        private CaliperPoint? _caliperEnd;
        /// <summary>
        /// 卡尺游标笔刷（虚线，用于绘制垂直游标线与连接线）。
        /// </summary>
        private SKPaint _pencaliper;
        /// <summary>
        /// 卡尺测量数值标签字体。
        /// </summary>
        private SKFont _fontcaliper;
        /// <summary>
        /// 卡尺是否正在拖拽中（鼠标按下后移动，释放后结束）。
        /// </summary>
        private bool _caliperDragging = false;
        #endregion

        #region 属性
        /// <summary>
        /// 每毫米所拥有的像素数量。
        /// </summary>
        public float Dpm { get; private set; }
        /// <summary>
        /// 速度（单位：mm/s）。
        /// </summary>
        public int Speed { get; set; } = 25;
        /// <summary>
        /// 是否显示 1mV 定标方波（默认开启）。
        /// <br/>国际标准要求每份心电图报告必须包含 1mV 定标方波，
        /// 用于校验振幅基准（标准：1mV 对应 10mm）。
        /// </summary>
        public bool ShowCalibration { get; set; } = true;
        /// <summary>
        /// 渲染主题（颜色、线宽、抗锯齿）。
        /// <br/>修改后调用 <see cref="ApplyTheme"/> 重建笔刷生效。
        /// </summary>
        public EPGRenderTheme Theme { get; set; } = new EPGRenderTheme();
        /// <summary>
        /// 导联着色模式（默认统一颜色）。
        /// <br/>设为 <see cref="LeadColorMode.ByGroup"/> 后调用 <see cref="ApplyTheme"/> 生效。
        /// </summary>
        public LeadColorMode LeadColorMode { get; set; } = LeadColorMode.Uniform;
        /// <summary>
        /// 按导联分组的颜色配置（仅 <see cref="LeadColorMode.ByGroup"/> 模式下生效）。
        /// </summary>
        public LeadGroupColors LeadGroupColors { get; set; } = new LeadGroupColors();

        /// <inheritdoc cref="_xmaxpoints"/>
        public int[] XmaxPoints => _xmaxpoints;
        /// <summary>
        /// 实际绘制左边距
        /// </summary>
        public float Left => _xdelta / 2;
        /// <summary>
        /// 实际绘制上边距
        /// </summary>
        public float Top => _ydelta / 2;
        /// <summary>
        /// 实际绘制宽度
        /// </summary>
        public float ActualWidth => _iwidth - _xdelta;
        /// <summary>
        /// 实际绘制高度
        /// </summary>
        public float ActualHeight => _iheight - _ydelta;
        /// <inheritdoc cref="_isprinting"/>
        public bool IsPrinting
        {
            get => _isprinting;
            set => _isprinting = value;
        }
        /// <inheritdoc cref="_offsets"/>
        public int[] Offsets => _offsets;
        /// <summary>
        /// 增益，单位：毫米每毫伏（mm/mV），默认 10mm/mV（标准增益）。
        /// <br/>setter 必须用浮点除法：value 是 int，若写 value / 10 则为整数除法，
        /// 设置为 5mm/mV 时会算成 0（5/10=0），导致波形幅值被乘 0 压成平线、
        /// 1mV 定标方波高度也为 0。
        /// </summary>
        public int Gain
        {
            get => (int)(_gain * 10);
            set => _gain = value / 10.0;
        }
        /// <summary>
        /// 是否启用手动标测（卡尺模式）。
        /// <br/>启用后可通过 <see cref="SetCaliper"/> 设置测量两点，
        /// 渲染时绘制双游标卡尺并显示时间间期、振幅差与心率。
        /// </summary>
        public bool CaliperEnabled { get; set; } = false;
        /// <summary>
        /// 卡尺颜色（游标线与标签），默认红色。
        /// </summary>
        public SKColor CaliperColor
        {
            get => _pencaliper.Color;
            set => _pencaliper.Color = value;
        }
        /// <inheritdoc cref="_caliperStart"/>
        public CaliperPoint? CaliperStart => _caliperStart;
        /// <inheritdoc cref="_caliperEnd"/>
        public CaliperPoint? CaliperEnd => _caliperEnd;
        /// <summary>
        /// 卡尺标签字体大小（像素）
        /// </summary>
        public float CaliperFontSize
        {
            get => _fontcaliper.Size;
            set => _fontcaliper.Size = value;
        }
        /// <summary>
        /// 水平缩放因子（1.0=原始大小，2.0=2倍放大，0.5=缩小一半）。
        /// <br/>影响每采样点的像素间距，放大时可见采样点减少。
        /// </summary>
        public double ZoomX { get; set; } = 1.0;
        /// <summary>
        /// 垂直缩放因子（1.0=原始大小，2.0=2倍放大，0.5=缩小一半）。
        /// <br/>影响波形振幅显示，放大时波形变高。
        /// </summary>
        public double ZoomY { get; set; } = 1.0;

        #endregion

        #region EPGRender：生理电图渲染器类构造函数
        /// <summary>
        /// 生理电图渲染器类构造函数。
        /// </summary>
        /// <remarks>
        /// 初始化 Dpm(像素/毫米)和导联标签字体后，调用 <see cref="ApplyTheme"/> 创建所有笔刷。
        /// 默认使用系统匹配的中英文字体和标准主题配色。
        /// </remarks>
        /// <param name="dpm">每毫米像素数(Dots per mm)，决定渲染精度。
        /// <br/>常用值: 屏幕 3.78(96dpi)、打印 11.81(300dpi)、高清 23.62(600dpi)</param>
        /// <param name="headersize">导联标签字体大小(像素)，默认 16</param>
        public EPGRender(double dpm, int headersize = 16)
        {
            Dpm = (float)dpm;

            _fontheader = new SKFont
            {
                Size = headersize,
                Typeface = _usen
            };

            // 根据主题创建所有笔刷
            ApplyTheme();
        }
        #endregion

        #region ApplyTheme：根据主题重建所有笔刷
        /// <summary>
        /// 根据当前 <see cref="Theme"/>、<see cref="LeadColorMode"/> 和 <see cref="LeadGroupColors"/>
        /// 重建所有笔刷。修改主题配置后调用此方法生效。
        /// </summary>
        public void ApplyTheme()
        {
            // 释放旧笔刷(避免内存泄漏，ApplyTheme 可能被多次调用)
            _pen1mm?.Dispose();
            _pen5mm?.Dispose();
            _penwave?.Dispose();
            _penheader?.Dispose();
            _penbg?.Dispose();
            _pencaliper?.Dispose();
            _fontcaliper?.Dispose();

            // 释放按导联分组的笔刷数组(仅 ByGroup 模式下非 null)
            if (_leadPens != null)
            {
                foreach (var pen in _leadPens)
                    pen?.Dispose();

                _leadPens = null;
            }

            var t = Theme;  // 获取当前主题配置

            // 重建网格笔刷: 1mm 细线(浅色) + 5mm 粗线(深色)
            _pen1mm = new SKPaint
            {
                Color = t.ThinGridColor,
                StrokeWidth = t.ThinGridStrokeWidth,
                Style = SKPaintStyle.Stroke,
                IsAntialias = t.AntiAlias
            };
            _pen5mm = new SKPaint
            {
                Color = t.ThickGridColor,
                StrokeWidth = t.ThickGridStrokeWidth,
                Style = SKPaintStyle.Stroke,
                IsAntialias = t.AntiAlias
            };

            // 重建波形笔刷（统一颜色）
            _penwave = new SKPaint
            {
                Color = t.WaveColor,
                StrokeWidth = t.WaveStrokeWidth,
                Style = SKPaintStyle.Stroke,
                IsAntialias = t.AntiAlias
            };

            // 重建标签笔刷
            _penheader = new SKPaint
            {
                Color = t.HeaderColor,
                StrokeWidth = t.HeaderStrokeWidth,
                Style = SKPaintStyle.Stroke,
                IsAntialias = t.AntiAlias
            };

            // 重建背景笔刷
            _penbg = new SKPaint
            {
                Color = t.Background,
                Style = SKPaintStyle.Fill
            };

            // 按导联组着色：创建笔刷数组
            if (LeadColorMode == LeadColorMode.ByGroup)
            {
                var g = LeadGroupColors;
                _leadPens = new SKPaint[19]; // 0-17 对应 18 导联，18 对应次导联

                for (var i = 0; i < 19; i++)
                {
                    // 根据导联索引选择对应组的颜色
                    var color = i switch
                    {
                        >= 0 and <= 5 => g.Limb,        // 肢体导联 I/II/III/aVR/aVL/aVF
                        >= 6 and <= 11 => g.Precordial, // 胸前导联 V1-V6
                        >= 12 and <= 14 => g.Posterior, // 后壁导联 V7-V9
                        >= 15 and <= 17 => g.RightChest, // 右胸导联 V3R-V5R
                        _ => g.Limb                      // 次导联（索引 18）
                    };

                    _leadPens[i] = new SKPaint
                    {
                        Color = color,
                        StrokeWidth = t.WaveStrokeWidth,
                        Style = SKPaintStyle.Stroke,
                        IsAntialias = t.AntiAlias
                    };
                }
            }

            // 重建卡尺笔刷（虚线游标，用于手动标测测量）
            _pencaliper = new SKPaint
            {
                Color = SKColors.Red,
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke,
                IsAntialias = t.AntiAlias,
                PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
            };

            // 重建卡尺标签字体（英文字体，用于显示数值）
            _fontcaliper = new SKFont
            {
                Size = 12f,
                Typeface = _usen
            };
        }
        #endregion

        #region GetWavePen：获取指定导联的波形笔刷
        /// <summary>
        /// 获取指定导联的波形笔刷。
        /// <br/><see cref="LeadColorMode.Uniform"/> 模式返回统一笔刷；
        /// <see cref="LeadColorMode.ByGroup"/> 模式返回按导联组的笔刷。
        /// </summary>
        /// <param name="leadIdx">导联索引（0-17，18 为次导联）</param>
        /// <returns>波形笔刷</returns>
        private SKPaint GetWavePen(int leadIdx)
        {
            if (LeadColorMode == LeadColorMode.Uniform || _leadPens == null)
                return _penwave;

            if (leadIdx < 0 || leadIdx >= _leadPens.Length)
                return _penwave;

            return _leadPens[leadIdx];
        }
        #endregion

        #region 手动标测：卡尺设置与坐标转换
        /// <summary>
        /// 设置手动标测卡尺的两点。
        /// <br/>设置后，在下次 <see cref="DrawDiagWaves"/> 调用时绘制双游标卡尺并显示测量值。
        /// </summary>
        /// <param name="startLead">起点导联索引（0-17）</param>
        /// <param name="startIndex">起点数据索引（采样点）</param>
        /// <param name="endLead">终点导联索引（0-17）</param>
        /// <param name="endIndex">终点数据索引（采样点）</param>
        public void SetCaliper(int startLead, int startIndex, int endLead, int endIndex)
        {
            _caliperStart = new CaliperPoint(startLead, startIndex);
            _caliperEnd = new CaliperPoint(endLead, endIndex);
        }

        /// <summary>
        /// 清除手动标测卡尺。
        /// </summary>
        public void ClearCaliper()
        {
            _caliperStart = null;
            _caliperEnd = null;
            _caliperDragging = false;
        }

        /// <summary>
        /// 鼠标按下事件：设置卡尺起点并进入拖拽状态。
        /// <br/>由外层窗体的 MouseDown 事件调用。按下后起点与终点重合，
        /// 随后通过 <see cref="OnMouseMove"/> 拖拽至终点。
        /// </summary>
        /// <param name="x">屏幕 X 坐标（像素）</param>
        /// <param name="y">屏幕 Y 坐标（像素）</param>
        /// <returns>true=已设置起点，需调用 Invalidate 重绘；false=坐标无效或卡尺未启用</returns>
        public bool OnMouseDown(float x, float y)
        {
            if (!CaliperEnabled)
                return false;

            if (PointToCaliperPoint(x, y, out int lead, out int index))
            {
                // 起点与终点初始重合，后续 OnMouseMove 更新终点
                _caliperStart = new CaliperPoint(lead, index);
                _caliperEnd = new CaliperPoint(lead, index);
                _caliperDragging = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 鼠标移动事件：拖拽更新卡尺终点。
        /// <br/>由外层窗体的 MouseMove 事件调用。仅在 <see cref="OnMouseDown"/> 后的拖拽状态下生效。
        /// </summary>
        /// <param name="x">屏幕 X 坐标（像素）</param>
        /// <param name="y">屏幕 Y 坐标（像素）</param>
        /// <returns>true=终点已更新，需调用 Invalidate 重绘；false=未在拖拽或坐标无效</returns>
        public bool OnMouseMove(float x, float y)
        {
            if (!CaliperEnabled || !_caliperDragging)
                return false;

            if (PointToCaliperPoint(x, y, out int lead, out int index))
            {
                _caliperEnd = new CaliperPoint(lead, index);

                return true;
            }

            return false;
        }

        /// <summary>
        /// 鼠标释放事件：结束拖拽。
        /// <br/>由外层窗体的 MouseUp 事件调用。若两点重合（未实际拖拽），自动清除卡尺。
        /// </summary>
        /// <returns>true=卡尺状态已变更，需调用 Invalidate 重绘；false=无需重绘</returns>
        public bool OnMouseUp()
        {
            _caliperDragging = false;

            // 释放时两点重合（仅点击未拖拽），清除卡尺避免残留
            if (_caliperStart != null && _caliperEnd != null)
            {
                var s = _caliperStart!;
                var e = _caliperEnd!;

                if (s.LeadIndex == e.LeadIndex && s.DataIndex == e.DataIndex)
                {
                    _caliperStart = null;
                    _caliperEnd = null;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 鼠标双击事件：清除当前卡尺测量。
        /// <br/>由外层窗体的 MouseDoubleClick 事件调用。
        /// </summary>
        /// <returns>true=已清除卡尺，需调用 Invalidate 重绘；false=无卡尺可清除</returns>
        public bool OnMouseDoubleClick()
        {
            if (_caliperStart != null || _caliperEnd != null)
            {
                _caliperStart = null;
                _caliperEnd = null;
                _caliperDragging = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 鼠标离开事件：取消正在进行的拖拽。
        /// <br/>由外层窗体的 MouseLeave 事件调用。若拖拽未完成則清除卡尺，
        /// 已完成的测量（两点不重合）保持显示。
        /// </summary>
        public void OnMouseLeave()
        {
            // 拖拽中途离开：若两点重合（未实际拖拽），清除卡尺
            if (_caliperDragging && _caliperStart != null && _caliperEnd != null)
            {
                var s = _caliperStart!;
                var e = _caliperEnd!;

                if (s.LeadIndex == e.LeadIndex && s.DataIndex == e.DataIndex)
                {
                    _caliperStart = null;
                    _caliperEnd = null;
                }
            }

            _caliperDragging = false;
        }
        #endregion

        #region 局部缩放：水平/垂直放大
        /// <summary>
        /// 水平缩放（以指定 X 坐标为中心）。
        /// <br/>由外层窗体在 Ctrl+滚轮 事件中调用。缩放后调整 _offsets 保持鼠标位置对应的数据索引不变。
        /// </summary>
        /// <param name="factor">缩放因子（1.25=放大25%，0.8=缩小20%）</param>
        /// <param name="centerX">缩放中心 X 坐标（通常为鼠标位置）</param>
        public void ZoomHorizontal(double factor, float centerX)
        {
            // 1. 确定鼠标所在列
            int col = XToColumn(centerX);
            float ox = _oxs[col];

            // 2. 记录缩放前 centerX 对应的数据索引
            double oldXstep = _xstep * ZoomX;
            int dataIdx = _offsets[0] + (int)((centerX - ox) / oldXstep);

            // 3. 应用缩放（限制范围 0.25~10）
            ZoomX = Math.Max(0.25, Math.Min(10.0, ZoomX * factor));

            // 4. 调整 _offsets 保持 centerX 对应同一数据索引
            double newXstep = _xstep * ZoomX;
            int newOffset = dataIdx - (int)((centerX - ox) / newXstep);
            _offsets[0] = Math.Max(0, newOffset);
        }

        /// <summary>
        /// 垂直缩放。
        /// <br/>由外层窗体在 Shift+滚轮 事件中调用。仅修改振幅缩放因子，不影响数据偏移。
        /// </summary>
        /// <param name="factor">缩放因子（1.25=放大25%，0.8=缩小20%）</param>
        public void ZoomVertical(double factor)
        {
            ZoomY = Math.Max(0.25, Math.Min(10.0, ZoomY * factor));
        }

        /// <summary>
        /// 重置缩放至原始大小（ZoomX=1.0, ZoomY=1.0）。
        /// </summary>
        public void ResetZoom()
        {
            ZoomX = 1.0;
            ZoomY = 1.0;
        }

        /// <summary>
        /// 根据屏幕 X 坐标确定所在列索引（对应 _oxs 数组下标）。
        /// </summary>
        /// <param name="x">屏幕 X 坐标</param>
        /// <returns>列索引（0/1/2/3）</returns>
        private int XToColumn(float x)
        {
            // 列分界取"区域边界"= 下一列起点 _oxs[c+1] - 导联头宽度 _hwidth:
            //   第 c 列波形实际绘制区为 [_oxs[c], _oxs[c+1] - _hwidth](画满整列)，
            //   [_oxs[c+1] - _hwidth, _oxs[c+1]) 为第 c+1 列导联头(标签+定标方波)区域。
            // 注意: 不能按列起点中点分界——那会把第 c 列后半段(末尾"倒手"处)误判到
            // 第 c+1 列，导致卡尺在该区域选错导联、算错数据索引。
            return _mode switch
            {
                LeadMode.LM_6x2 or LeadMode.LM_6x2x1 =>
                    x < _oxs[2] - _hwidth ? 0 : 2,
                LeadMode.LM_6x3 or LeadMode.LM_6x3x1 =>
                    x < _oxs[1] - _hwidth ? 0 :
                    x < _oxs[2] - _hwidth ? 1 : 2,
                LeadMode.LM_3x4 =>
                    x < _oxs[1] - _hwidth ? 0 :
                    x < _oxs[2] - _hwidth ? 1 :
                    x < _oxs[3] - _hwidth ? 2 : 3,
                _ => 0
            };
        }
        #endregion

        #region PointToCaliperPoint：屏幕坐标转卡尺测量点
        /// <summary>
        /// 将屏幕坐标转换为卡尺测量点（导联索引 + 数据索引）。
        /// <br/>由外层窗体在鼠标事件中调用，将鼠标位置映射到波形数据点。
        /// </summary>
        /// <param name="x">屏幕 X 坐标（像素）</param>
        /// <param name="y">屏幕 Y 坐标（像素）</param>
        /// <param name="leadIndex">输出：最近的导联索引（0-17）</param>
        /// <param name="dataIndex">输出：对应的数据索引（采样点）</param>
        /// <returns>true=成功映射；false=坐标不在有效波形区域内</returns>
        public bool PointToCaliperPoint(float x, float y, out int leadIndex, out int dataIndex)
        {
            leadIndex = -1;
            dataIndex = -1;

            if (_paths == null || _paths.Count == 0 || _xstep <= 0)
                return false;

            // 1. 先根据 X 坐标确定所在列（避免多列模式下 Y 基坐标相同导致误判）
            int col = XToColumn(x);
            if (col < 0 || col >= _oxs.Length)
                return false;

            // 2. 确定该列包含的导联索引范围
            int leadStart, leadEnd;

            switch (_mode)
            {
                case LeadMode.LM_6x2 or LeadMode.LM_6x2x1:
                    // 左列=导联0-5，右列=导联6-11
                    leadStart = col == 0 ? 0 : 6;
                    leadEnd = leadStart + 6;
                    break;
                case LeadMode.LM_6x3 or LeadMode.LM_6x3x1:
                    // 左/中/右列各6个导联
                    leadStart = col * 6;
                    leadEnd = leadStart + 6;
                    break;
                case LeadMode.LM_3x4:
                    // 4 列(列优先): 该列导联 = 3c, 3c+1, 3c+2(连续 3 个)
                    leadStart = col * 3;
                    leadEnd = leadStart + 3;
                    break;
                default: // LM_12x1 及单导联
                    leadStart = 0;
                    leadEnd = 12;
                    break;
            }

            // 3. 在该列范围内根据 Y 坐标找最近导联基线
            //    各模式列内导联均为连续区间，直接遍历 leadStart~leadEnd，基线取 _ybases[j]
            //    (Reset 已按导联索引存放行基线，LM_3x4 中同列 3 个导联分属 3 个不同行)
            int bestLead = -1;
            float bestDist = float.MaxValue;

            for (int j = leadStart; j < leadEnd; j++)
            {
                float dist = Math.Abs(y - _ybases[j]);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestLead = j;
                }
            }

            if (bestLead < 0)
                return false;

            // 3.5 次导联行判定(x1 模式): 若 y 更接近次导联基线(_ybases[18])，
            //     则映射到次导联(索引 18)：数据源 _secidx、列起点 _oxs[0](横跨整宽)、偏移 _offsets[1]
            if (_mode == LeadMode.LM_6x2x1 || _mode == LeadMode.LM_6x3x1)
            {
                float distSec = Math.Abs(y - _ybases[18]);

                if (distSec < bestDist)
                {
                    leadIndex = 18;
                    dataIndex = Math.Max(0, _offsets[1] +
                        (int)Math.Round((x - _oxs[0]) / (_xstep * ZoomX)));

                    return true;
                }
            }

            // 4. 反推数据索引：dataIndex = offsets[0] + (x - oxs[col]) / (xstep * ZoomX)
            //    主列导联均使用 _offsets[0] 作为滚动偏移；ZoomX 影响每采样点像素间距
            //    使用 Math.Round 四舍五入(而非截断)，反算游标位置误差减半
            dataIndex = _offsets[0] + (int)Math.Round((x - _oxs[col]) / (_xstep * ZoomX));

            if (dataIndex < 0)
                dataIndex = 0;

            leadIndex = bestLead;
            return true;
        }

        /// <summary>
        /// 获取当前卡尺的测量结果。
        /// </summary>
        /// <param name="edata">波形数据（与 DrawDiagWaves 传入的同一份数据）</param>
        /// <returns>
        /// 元组 (TimeMs, AmpMv, HrBpm)：
        /// <list type="bullet">
        /// <item><term>TimeMs</term><description>两点时间差（毫秒）</description></item>
        /// <item><term>AmpMv</term><description>两点振幅差（毫伏，正值表示起点高于终点）</description></item>
        /// <item><term>HrBpm</term><description>由时间差换算的瞬时心率（次/分）</description></item>
        /// </list>
        /// </returns>
        public (double TimeMs, double AmpMv, double HrBpm) GetMeasurement(double[][] edata)
        {
            var data = edata?.Select(v => v.ToArray<double, float>()).ToList();
            return GetMeasurement(data);
        }

        /// <inheritdoc cref="GetMeasurement(double[][])"/>
        public (double TimeMs, double AmpMv, double HrBpm) GetMeasurement(List<float[]> edata)
        {
            if (_caliperStart == null || _caliperEnd == null || edata == null)
                return (0, 0, 0);

            var s = _caliperStart!;
            var e = _caliperEnd!;

            // 时间差（ms）：采样点差 × 1000 / 绘图频率（_drawfreq 即采样率）
            int sampleDelta = Math.Abs(e.DataIndex - s.DataIndex);
            double timeMs = sampleDelta * 1000.0 / _drawfreq;

            // 心率（bpm）：60000ms / 周期(ms)
            double hrBpm = timeMs > 0 ? 60000.0 / timeMs : 0;

            // 振幅差（mV）：通过屏幕像素差反推
            // 标准增益 10mm/mV 时 1mV = 10 × Dpm 像素；Gain 为增益因子，在增益下放大 Gain 倍
            // 故 mV = 像素差 / (10 × Dpm × Gain)
            // 次导联点(LeadIndex==18): 数据源取 _secidx，基线取 _ybases[18]
            int sLead = s.LeadIndex == 18 ?
                Math.Max(0, Math.Min(_secidx, edata.Count - 1)) :
                Math.Max(0, Math.Min(s.LeadIndex, edata.Count - 1));
            int eLead = e.LeadIndex == 18 ?
                Math.Max(0, Math.Min(_secidx, edata.Count - 1)) :
                Math.Max(0, Math.Min(e.LeadIndex, edata.Count - 1));
            int sIdx = Math.Max(0, Math.Min(s.DataIndex, edata[sLead].Length - 1));
            int eIdx = Math.Max(0, Math.Min(e.DataIndex, edata[eLead].Length - 1));

            // 屏幕坐标按"基线 - 幅值"计算(与波形绘制一致，正振幅向上)
            float y1 = _ybases[YBaseIndex(s.LeadIndex)] - edata[sLead][sIdx].ToPixel(Dpm, _gain * ZoomY);
            float y2 = _ybases[YBaseIndex(e.LeadIndex)] - edata[eLead][eIdx].ToPixel(Dpm, _gain * ZoomY);
            // 振幅差(mV) = 终点相对起点的高度差(终点高于起点为正)；
            // ZoomY 在分子(y1-y2)和分母中约去，结果为物理量不受缩放影响
            double ampMv = (y1 - y2) / (10.0 * Dpm * _gain * ZoomY);

            return (timeMs, ampMv, hrBpm);
        }

        /// <summary>
        /// 获取导联的行基线索引（_ybases 数组下标）。
        /// <br/>Reset 已按导联索引直接存放各导联的行基线
        /// (多列同行的导联基线相同，LM_3x4 的行 = 导联%3 也已展开到每个导联)，
        /// 故直接返回导联索引；次导联(18)同理直接返回。
        /// </summary>
        /// <param name="leadIdx">导联索引（0-17，18 为次导联）</param>
        /// <returns>_ybases 下标</returns>
        private int YBaseIndex(int leadIdx) => leadIdx;

        /// <summary>
        /// 根据导联索引确定其所在列索引（对应 _oxs 数组下标）。
        /// </summary>
        /// <param name="leadIdx">导联索引（0-17）</param>
        /// <returns>列索引（0/1/2/3），对应 _oxs[0..3]</returns>
        private int GetLeadColumn(int leadIdx)
        {
            return _mode switch
            {
                LeadMode.LM_6x2 or LeadMode.LM_6x2x1 => leadIdx < 6 ? 0 : 2,
                LeadMode.LM_6x3 or LeadMode.LM_6x3x1 => leadIdx < 6 ? 0 : leadIdx < 12 ? 1 : 2,
                // 3x4 列优先: 列 = 导联/3
                LeadMode.LM_3x4 => leadIdx / 3,
                LeadMode.LM_12x1 => 0,
                _ => 0
            };
        }
        #endregion

        #region Reset：重置参数
        /// <summary>
        /// 重置渲染参数，在画布尺寸、导联模式或绘图频率变化时调用。
        /// </summary>
        /// <remarks>
        /// <para>此方法计算并缓存所有坐标基量，避免在每次绘制时重复计算:</para>
        /// <list type="bullet">
        /// <item><c>_xdelta/_ydelta</c>: 网格校正量，确保大格(5mm)为整数</item>
        /// <item><c>_oxs[0..3]</c>: 各列的 X 轴起始坐标(含导联头宽度)</item>
        /// <item><c>_ybases[0..18]</c>: 各导联行的 Y 轴基坐标(波形基线位置)</item>
        /// <item><c>_xstep</c>: X 轴每采样点步长(像素) = 速度 × Dpm / 绘图频率</item>
        /// <item><c>_rtxs</c>: 实时模式各列的当前绘制 X 坐标(初始为列起始)</item>
        /// </list>
        /// <para>若参数未变化则跳过重算，但始终重置 _offsets 为 0。</para>
        /// </remarks>
        /// <param name="iwidth">画布宽度（像素）</param>
        /// <param name="iheight">画布高度（像素）</param>
        /// <param name="mode">导联布局模式</param>
        /// <param name="secidx">次导联索引（仅对 <see cref="LeadMode.LM_6x2x1"/> 和
        /// <see cref="LeadMode.LM_6x3x1"/> 有效，指定底部节律导联对应的导联序号）</param>
        /// <param name="drawfreq">绘图频率，如采样频率 500Hz，每5个点画一次，那么绘图频率为 100Hz，
        /// 即每秒画 50 个点。决定 X 轴每采样点的像素步长。</param>
        public void Reset(double iwidth, double iheight, LeadMode mode,
            int secidx = 0, int drawfreq = 50)
        {
            // 仅在画布尺寸/导联模式/次导联/绘图频率任一变化时才重算坐标基量
            if (_mode != mode || (mode == LeadMode.LM_6x2x1 && _secidx != secidx) ||
                (mode == LeadMode.LM_6x3x1 && _secidx != secidx) ||
                _iwidth != (int)iwidth || _iheight != (int)iheight ||
                _drawfreq != drawfreq)
            {
                _iwidth = (int)iwidth;
                _iheight = (int)iheight;
                _mode = mode;
                _secidx = secidx;
                _drawfreq = drawfreq;

                #region 初始化 X 轴基坐标
                // _xdelta/_ydelta: 画布尺寸对 5mm 大格取余，用于将起始坐标向右下偏移半个余量，
                //                  确保画出的网格大格(5×5 小格)为完整整数个
                _xdelta = _iwidth % (5 * Dpm);
                _ydelta = _iheight % (5 * Dpm);

                // _xstep: X 轴每采样点的像素步长 = 走纸速度(mm/s) × Dpm(像素/mm) / 绘图频率(点/s)
                //         例: 25mm/s × 11.81px/mm / 50点/s = 5.9 像素/点
                _xstep = Speed * Dpm / _drawfreq;

                // 左列(或单列)起始 X = 左边距 + 导联头宽度(10mm)
                // 先统一复位所有列起点为左列基线，避免切换模式时残留上一模式的旧值。
                // 否则若某模式未显式赋值 _oxs[1]/_oxs[2]/_oxs[3]，这些槽位会保留上一次
                // 其它模式的值；一旦后续代码误引用它们，第二/三列的导联头或波形就会
                // 错误地落到第一列位置(即"标尺画到第一行第一列"类问题)。
                _oxs[0] = _oxs[1] = _oxs[2] = _oxs[3] = _xdelta / 2 + _hwidth;

                // 2 列模式: 右列起始 X = 画布宽度的一半 + 导联头宽度
                if (_mode == LeadMode.LM_6x2 || _mode == LeadMode.LM_6x2x1)
                    _oxs[2] = _iwidth / 2 + _hwidth;
                // 3 列模式: 中列和右列起始 X 分别在 1/3 和 2/3 处
                if (_mode == LeadMode.LM_6x3 || _mode == LeadMode.LM_6x3x1)
                {
                    _oxs[1] = (_iwidth - _xdelta) / 3 + _hwidth;
                    _oxs[2] = (_iwidth - _xdelta) * 2 / 3 + _hwidth;
                }
                // 4 列模式(3x4): 四列均匀分布在画布的 1/4, 2/4, 3/4, 4/4 处
                if (_mode == LeadMode.LM_3x4)
                {
                    _oxs[0] = _xdelta / 2 + _hwidth;
                    _oxs[1] = (_iwidth - _xdelta) / 4 + _hwidth;
                    _oxs[2] = (_iwidth - _xdelta) / 2 + _hwidth;
                    _oxs[3] = (_iwidth - _xdelta) * 3 / 4 + _hwidth;
                }
                // LM_12x1 单列: 仅 _oxs[0] 有效(已设为基线值)，_oxs[1..3] 保持基线值(未被引用)

                // 实时模式: 各列的当前绘制 X 坐标初始化为列起始位置
                // 第 4 个位置: LM_3x4 为第 4 列(_oxs[3])；其他模式供次导联使用(_oxs[0])
                _rtxs = new float[]
                {
                    _oxs[0], _oxs[1], _oxs[2],
                    _mode == LeadMode.LM_3x4 ? _oxs[3] : _oxs[0]
                };
                #endregion

                #region 初始化 Y 轴基坐标
                // _ybases[i]: 第 i 个导聧行的 Y 轴基坐标(波形基线)
                // 多列模式下同一行的导联共享相同 Y 基坐标(如 LM_6x2 中导联 0 和导联 6 同行)
                var xhalf‌delta = _xdelta / 2;
                var yhalf‌delta = _ydelta / 2;

                // 按导联模式计算各行的 Y 基坐标
                switch (_mode)
                {
                    case LeadMode.LM_6x2:
                    case LeadMode.LM_6x3:
                        {
                            // 6 行均分画布高度，每行间距 = (画布高 - 上下边距) / 6
                            var delta = (_iheight - _ydelta) / 6;

                            for (var i = 0; i < 18; i++)
                            {
                                // 前 6 个导联各占一行；后续导联复用同行 Y 基坐标(多列同行)
                                _ybases[i] = i < 6 ? (float)((i + 0.5) * delta + yhalfdelta) :
                                    _ybases[i - 6];
                            }
                        }
                        break;
                    case LeadMode.LM_6x2x1:
                    case LeadMode.LM_6x3x1:
                        {
                            // 7 行: 6 行主导联 + 1 行次导联(节律导联)
                            var delta = (_iheight - _ydelta) / 7;

                            for (var i = 0; i < 18; i++)
                            {
                                _ybases[i] = i < 6 ? (float)((i + 0.5) * delta + yhalfdelta) :
                                    _ybases[i - 6];
                            }

                            // 次导联 Y 基坐标: 第 7 行中央(6.5 × 行间距)
                            _ybases[18] = yhalfdelta + (float)(6.5 * delta);
                        }
                        break;
                    case LeadMode.LM_3x4:
                        {
                            // 3 行 × 4 列(列优先，与 DrawHeaders 一致): 行 = 导联%3，列 = 导联/3
                            // 行 0: I,aVR,V1,V4   行 1: II,aVL,V2,V5   行 2: III,aVF,V3,V6
                            // _ybases 按导联索引直接存放其行基线(j%3 行)
                            var delta = (_iheight - _ydelta) / 3.0;

                            for (int j = 0; j < 12; j++)
                                _ybases[j] = (float)((j % 3 + 0.5) * delta + yhalfdelta);
                        }
                        break;
                    case LeadMode.LM_12x1:
                        {
                            // 12 行 × 1 列: 每个导联独占一行，行间距 = (画布高 - 上下边距) / 12
                            var delta = (_iheight - _ydelta) / 12;

                            for (int i = 0; i < 12; i++)
                                _ybases[i] = (float)((i + 0.5) * delta + yhalfdelta);
                        }
                        break;
                    default:
                        // 单导联模式: Y 基坐标 = 画布中央偏下 10 像素
                        _ybases[(int)_mode] = (float)(_iheight * 0.5 + 10);
                        break;
                }

                _isreset = true;
                #endregion
            }

            // 无论参数是否变化，始终重置滚动偏移为 0
            _offsets.Zero();
        }
        #endregion

        #region DrawGrid：绘制背景网格与导联标签
        /// <summary>
        /// 绘制心电图纸背景网格（1mm 细线 + 5mm 粗线）并叠加导联标签。
        /// </summary>
        /// <remarks>
        /// <para>标准心电图纸规格: 每 1mm 一条细线，每 5mm 一条粗线(大格)，
        /// 横向 1mm = 0.04s(25mm/s 走纸)，纵向 1mm = 0.1mV(10mm/mV 增益)。</para>
        /// <para>非打印模式下先清空背景色，打印模式下保留已有内容(PDF 多页叠加时不覆盖)。</para>
        /// </remarks>
        /// <param name="canvas">SkiaSharp 画布</param>
        private void DrawGrid(SKCanvas canvas)
        {
            if (canvas != null)
            {
                var xdelta = _xdelta / 2;
                var ydelta = _ydelta / 2;

                // 非打印模式: 用背景色清空画布; 打印模式: 不清空(PDF 页面已为白底)
                if (!_isprinting)
                    canvas.Clear(_penbg.Color);

                #region 画网格线
                var xwidth = _iwidth - xdelta / 2;
                var yheight = _iheight - ydelta / 2;

                var index = 0;

                // 画水平线: 从上到下每隔 1mm 一条，每 5 条用粗线(5mm 大格线)
                for (var y = ydelta; y <= yheight; y += Dpm)
                {
                    var hpen = index++ % 5 != 0 ? _pen1mm : _pen5mm;

                    canvas.DrawLine(xdelta, y, _iwidth - xdelta, y, hpen);
                }

                index = 0;

                // 画垂直线: 从左到右每隔 1mm 一条，每 5 条用粗线(5mm 大格线)
                for (var x = xdelta; x <= xwidth; x += Dpm)
                {
                    var vpen = index++ % 5 != 0 ? _pen1mm : _pen5mm;

                    canvas.DrawLine(x, ydelta, x, _iheight - ydelta, vpen);
                }
                #endregion

                // 绘制导联标签(导联名称 + 1mV 定标方波)
                DrawHeaders(canvas, _secidx);
            }
        }

        /// <summary>
        /// 绘制导联标签与 1mV 定标方波。
        /// </summary>
        /// <remarks>
        /// <para>按导联布局模式在每个导联回路的起始位置绘制:</para>
        /// <list type="bullet">
        /// <item>1mV 定标方波: 矩形脉冲，高度 = 10mm × Dpm × Gain，用于校验振幅基准</item>
        /// <item>导联名称文字: 显示在定标方波上方(如 "I", "aVR", "V1" 等)</item>
        /// </list>
        /// </remarks>
        /// <param name="canvas">SkiaSharp 画布</param>
        /// <param name="secidx">次导联索引（仅对 <see cref="LeadMode.LM_6x2x1"/> 和
        /// <see cref="LeadMode.LM_6x3x1"/> 有效）</param>
        private void DrawHeaders(SKCanvas canvas, int secidx)
        {
            switch (_mode)
            {
                case LeadMode.LM_6x2:
                    {
                        for (var i = 0; i < 6; i++)
                        {
                            DrawHeader(_labels[i], _oxs[0], _ybases[i]);
                            DrawHeader(_labels[i + 6], _oxs[2], _ybases[i]);
                        }
                    }
                    break;
                case LeadMode.LM_6x2x1:
                    {
                        for (var i = 0; i < 6; i++)
                        {
                            DrawHeader(_labels[i], _oxs[0], _ybases[i]);
                            DrawHeader(_labels[i + 6], _oxs[2], _ybases[i]);
                        }

                        DrawHeader(_labels[secidx], _oxs[0], _ybases[18]);
                    }
                    break;
                case LeadMode.LM_6x3:
                    {
                        for (var i = 0; i < 6; i++)
                        {
                            DrawHeader(_labels[i], _oxs[0], _ybases[i]);
                            DrawHeader(_labels[i + 6], _oxs[1], _ybases[i]);
                            DrawHeader(_labels[i + 12], _oxs[2], _ybases[i]);
                        }
                    }
                    break;
                case LeadMode.LM_6x3x1:
                    {
                        for (var i = 0; i < 6; i++)
                        {
                            DrawHeader(_labels[i], _oxs[0], _ybases[i]);
                            DrawHeader(_labels[i + 6], _oxs[1], _ybases[i]);
                            DrawHeader(_labels[i + 12], _oxs[2], _ybases[i]);
                        }

                        DrawHeader(_labels[secidx], _oxs[0], _ybases[18]);
                    }
                    break;
                case LeadMode.LM_3x4:
                    {
                        // 3 行 × 4 列：12 导联(列优先，与 Reset/DrawWaves 一致)
                        // 行 = i%3，列 = i/3: 列 0: I,II,III   列 1: aVR,aVL,aVF
                        //                     列 2: V1,V2,V3   列 3: V4,V5,V6
                        // _ybases[i] 已按导联索引直接存放其行基线(行 i%3)
                        for (int i = 0; i < 12; i++)
                        {
                            int col = i / 3;
                            DrawHeader(_labels[i], _oxs[col], _ybases[i]);
                        }
                    }
                    break;
                case LeadMode.LM_12x1:
                    {
                        // 12 行 × 1 列：12 导联纵向排列
                        for (int i = 0; i < 12; i++)
                            DrawHeader(_labels[i], _oxs[0], _ybases[i]);
                    }
                    break;
                default:
                    DrawHeader(_labels[(int)_mode], _oxs[0], _ybases[(int)_mode]);
                    break;
            }

            // 局部函数: 在指定坐标绘制导联标签(1mV 定标方波 + 导联名称)
            void DrawHeader(string header, float x, float y)
            {
                // yhalf‌delta: 1mV 对应的像素高度 = 增益 × 10mm × Dpm(像素/mm)
                //         标准增益(1.0)时 1mV = 10mm × Dpm 像素
                var ydelta = (float)(_gain * Dpm * 10);

                if (ShowCalibration)
                {
                    using var builder = new SKPathBuilder();

                    // 定标方波轮廓: 6 个点构成的矩形脉冲(左→右)
                    //   起点(x-5mm) → 平移到(x-3.5mm) → 上升1mV → 平移到(x-1.5mm) → 下降回基线 → 终点(x)
                    var points = new SKPoint[]
                    {
                        new SKPoint(x - 5 * Dpm, y),           // 基线左端
                        new SKPoint(x - 3.5f * Dpm, y),       // 基线(脉冲起点)
                        new SKPoint(x - 3.5f * Dpm, y - ydelta), // 脉冲顶点(上升 1mV)
                        new SKPoint(x - 1.5f * Dpm, y - ydelta), // 脉冲顶部(平移)
                        new SKPoint(x - 1.5f * Dpm, y),       // 基线(脉冲终点,下降)
                        new SKPoint(x, y)                      // 基线右端
                    };

                    builder.AddPoly(points, false);

                    using var path = builder.Detach();
                    canvas.DrawPath(path, _penheader);
                }

                // 绘制导联名称文字(右对齐，位于定标方波上方)
                canvas.DrawText(header, x - 5 * Dpm, y - ydelta +
                    _fontheader.Metrics.CapHeight, SKTextAlign.Right,
                    _fontheader, _penheader);
            }
        }
        #endregion

        #region DrawWaves：绘制实时生理电图波形
        /// <summary>
        /// 绘制实时滚动心电图波形（在线模式）。
        /// </summary>
        /// <remarks>
        /// <para>每次调用从队列中取出一个采样点(所有导联同一时刻的值)，
        /// 在各导联回路的当前 X 位置画一段线段，然后 X 位置前进一步。</para>
        /// <para>当 X 位置到达列右边界时自动回到列起始位置(波形循环滚动)，
        /// 并通过 <see cref="Erase"/> 擦除即将绘制的区域，实现滚动效果。</para>
        /// <para>若队列为空则重绘网格(等待数据)。</para>
        /// </remarks>
        /// <param name="canvas">SkiaSharp 画布</param>
        /// <param name="edata">心电数据队列(线程安全)，每项为 float[导联数] 单帧采样</param>
        public void DrawWaves(SKCanvas canvas, AdvQueue<float[]> edata)
        {
            // 首次或参数变化后重置时，重绘网格背景
            if (_isreset)
            {
                _isreset = false;

                DrawGrid(canvas);
            }

            // 从队列取出一个采样点(非阻塞，无数据时返回 false)
            if (edata?.Dequeue(out var dv) == true)
            {
                switch (_mode)
                {
                    case LeadMode.LM_6x2:
                    case LeadMode.LM_6x2x1:
                        #region 画左、右列导联
                        // 画左列导联(导联 0-5): 从上一次 Y 位置画线到当前 Y 位置
                        // _rtys[i]: 上一帧第 i 导联的值; dv[i]: 当前帧第 i 导联的值
                        // ToPixel: 将 ADC 原始值转换为像素偏移(相对于 Y 基坐标)
                        for (var i = 0; i < 6; i++)
                        {
                            canvas.DrawLine(
                                _rtxs[0],                                  // 起点 X: 左列当前绘制位置
                                _ybases[i] - _rtys[i].ToPixel(Dpm, _gain),  // 起点 Y: 基坐标 - 上帧值
                                _rtxs[0] + _xstep,                          // 终点 X: 前进一个步长
                                _ybases[i] - dv[i].ToPixel(Dpm, _gain),     // 终点 Y: 基坐标 - 当前值
                                GetWavePen(i));
                            _rtys[i] = dv[i];  // 缓存当前值供下一帧使用
                        }

                        _rtxs[0] += _xstep;  // 左列 X 位置前进一步

                        // 画右列导联(导联 6-11): 逻辑同左列，使用 _oxs[2] 作为右列基坐标
                        for (var i = 6; i < 12; i++)
                        {
                            canvas.DrawLine(
                                _rtxs[2],
                                _ybases[i] - _rtys[i].ToPixel(Dpm, _gain),
                                _rtxs[2] + _xstep,
                                _ybases[i] - dv[i].ToPixel(Dpm, _gain),
                                GetWavePen(i));
                            _rtys[i] = dv[i];
                        }

                        _rtxs[2] += _xstep;

                        // 回绕检测: 当左列 X 位置即将超过画布中线时，重置左右列到起始位置
                        // 实现波形循环滚动效果(新波形覆盖旧波形区域)
                        if (_rtxs[0] + _xstep >= (_iwidth - _xdelta) / 2)
                        {
                            _rtxs[0] = _oxs[0];
                            _rtxs[2] = _oxs[2];
                        }
                        #endregion

                        #region 画次导联(节律导联，仅 LM_6x2x1 模式)
                        // 次导联: 底部单独一行，显示 _secidx 指定的导联(通常为 II 导联)
                        if (_mode == LeadMode.LM_6x2x1)
                        {
                            canvas.DrawLine(
                                _rtxs[3],                                          // 起点 X: 次导联当前绘制位置
                                _ybases[18] - _rtys[18].ToPixel(Dpm, _gain),      // 起点 Y: 次导联基坐标 - 上帧值
                                _rtxs[3] + _xstep,                                  // 终点 X: 前进一个步长
                                _ybases[18] - dv[_secidx].ToPixel(Dpm, _gain),     // 终点 Y: 次导联基坐标 - 当前值
                                GetWavePen(_secidx));

                            _rtys[18] = dv[_secidx];
                            _rtxs[3] += _xstep;

                            // 主导联坐标复位
                            if (_rtxs[3] + _xstep >= _iwidth - _xdelta / 2)
                            {
                                _rtxs[3] = _oxs[0];
                            }
                        }
                        #endregion
                        break;
                    case LeadMode.LM_6x3:
                    case LeadMode.LM_6x3x1:
                        #region 画左、右列导联
                        // 画左列导联
                        for (var i = 0; i < 6; i++)
                        {
                            canvas.DrawLine(
                                _rtxs[0],
                                _ybases[i] - _rtys[i].ToPixel(Dpm, _gain),
                                _rtxs[0] + _xstep,
                                _ybases[i] - dv[i].ToPixel(Dpm, _gain),
                                GetWavePen(i));
                            _rtys[i] = dv[i];
                        }

                        _rtxs[0] += _xstep;

                        // 画中列导联
                        for (var i = 6; i < 12; i++)
                        {
                            canvas.DrawLine(
                                _rtxs[1],
                                _ybases[i] - _rtys[i].ToPixel(Dpm, _gain),
                                _rtxs[1] + _xstep,
                                _ybases[i] - dv[i].ToPixel(Dpm, _gain),
                                GetWavePen(i));
                            _rtys[i] = dv[i];
                        }

                        _rtxs[1] += _xstep;

                        // 画右列导联
                        for (var i = 12; i < 18; i++)
                        {
                            canvas.DrawLine(
                                _rtxs[2],
                                _ybases[i] - _rtys[i].ToPixel(Dpm, _gain),
                                _rtxs[2] + _xstep,
                                _ybases[i] - dv[i].ToPixel(Dpm, _gain),
                                GetWavePen(i));
                            _rtys[i] = dv[i];
                        }

                        _rtxs[2] += _xstep;

                        // 左右导联坐标复位
                        if (_rtxs[0] + _xstep >= _iwidth / 3 + _xdelta / 6)
                        {
                            _rtxs[0] = _oxs[0];
                            _rtxs[1] = _oxs[1];
                            _rtxs[2] = _oxs[2];
                        }
                        #endregion

                        #region 画主导联
                        if (_mode == LeadMode.LM_6x3x1)
                        {
                            canvas.DrawLine(
                                _rtxs[3],
                                _ybases[18] - _rtys[18].ToPixel(Dpm, _gain),
                                _rtxs[3] + _xstep,
                                _ybases[18] - dv[_secidx].ToPixel(Dpm, _gain),
                                GetWavePen(_secidx));

                            _rtys[18] = dv[_secidx];
                            _rtxs[3] += _xstep;

                            // 主导联坐标复位
                            if (_rtxs[3] + _xstep >= _iwidth - _xdelta / 2)
                            {
                                _rtxs[3] = _oxs[0];
                            }
                        }
                        #endregion
                        break;
                    case LeadMode.LM_3x4:
                        #region 画 4 列导联(3 行 × 4 列，12 导联，列优先填充)
                        // 列优先映射(与 DrawHeaders/Reset 一致): 列 c 的导联 = 3c, 3c+1, 3c+2
                        // 行 = 导联 % 3(基线 _ybases[i] 已按导联索引存放)，列 = 导联 / 3(X 基坐标 _oxs[0..3])
                        // 列 0: I,II,III   列 1: aVR,aVL,aVF   列 2: V1,V2,V3   列 3: V4,V5,V6
                        for (var c = 0; c < 4; c++)
                        {
                            // 画第 c 列的 3 个导联(共享该列 X 位置 _rtxs[c]，行基线取 _ybases[i])
                            for (var r = 0; r < 3; r++)
                            {
                                // 列 c 第 r 行对应的导联索引
                                var i = c * 3 + r;

                                canvas.DrawLine(
                                    _rtxs[c],
                                    _ybases[i] - _rtys[i].ToPixel(Dpm, _gain),
                                    _rtxs[c] + _xstep,
                                    _ybases[i] - dv[i].ToPixel(Dpm, _gain),
                                    GetWavePen(i));
                                _rtys[i] = dv[i];
                            }

                            _rtxs[c] += _xstep;  // 第 c 列 X 位置前进一步
                        }

                        // 回绕检测: 当第 0 列到达列右边界时，4 列同时回绕到各自起始位置
                        // 列宽 = (画布宽 - 边距) / 4，列右边界 = 左边距 + 列宽
                        if (_rtxs[0] + _xstep >= _xdelta / 2 + (_iwidth - _xdelta) / 4)
                        {
                            _rtxs[0] = _oxs[0];
                            _rtxs[1] = _oxs[1];
                            _rtxs[2] = _oxs[2];
                            _rtxs[3] = _oxs[3];
                        }
                        #endregion
                        break;
                    case LeadMode.LM_12x1:
                        #region 画单列 12 导联(12 行 × 1 列)
                        // 12 个导联纵向排列，共用单列 X 位置 _rtxs[0]
                        for (var i = 0; i < 12; i++)
                        {
                            canvas.DrawLine(
                                _rtxs[0],
                                _ybases[i] - _rtys[i].ToPixel(Dpm, _gain),
                                _rtxs[0] + _xstep,
                                _ybases[i] - dv[i].ToPixel(Dpm, _gain),
                                GetWavePen(i));
                            _rtys[i] = dv[i];
                        }

                        _rtxs[0] += _xstep;

                        // 回绕检测: 到达画布右边界(不含右边距)时回到列起始
                        if (_rtxs[0] + _xstep >= _iwidth - _xdelta / 2)
                        {
                            _rtxs[0] = _oxs[0];
                        }
                        #endregion
                        break;
                    default:
                        #region 画单导联(枚举值 0-17 与导联索引一致)
                        // 数据源、Y 基坐标、笔刷均取 (int)_mode 指定的导联
                        canvas.DrawLine(
                            _rtxs[0],
                            _ybases[(int)_mode] - _rtys[(int)_mode].ToPixel(Dpm, _gain),
                            _rtxs[0] + _xstep,
                            _ybases[(int)_mode] - dv[(int)_mode].ToPixel(Dpm, _gain),
                            GetWavePen((int)_mode));

                        _rtys[(int)_mode] = dv[(int)_mode];

                        _rtxs[0] += _xstep;

                        // 回绕检测: 到达画布右边界(不含右边距)时回到列起始
                        if (_rtxs[0] + _xstep >= _iwidth - _xdelta / 2)
                        {
                            _rtxs[0] = _oxs[0];
                        }
                        #endregion
                        break;
                }

                Erase(canvas, _rtxs[0], _rtxs[1], _rtxs[2], _rtxs[3]);
            }
            else
                DrawGrid(canvas);
        }

        /// <summary>
        /// 擦除即将绘制的波形区域，实现滚动效果。
        /// </summary>
        /// <remarks>
        /// <para>实时模式下波形从左到右连续绘制，到达右边界后回绕到左侧。
        /// 回绕时需要擦除旧的波形痕迹，否则新波形会叠加在旧波形上。</para>
        /// <para>实现方式: 构建"禁止绘制区域"的 SKPath(导联头部 + 波形前沿的擦除带)，
        /// 对画布执行 ClipPath 裁剪后重绘网格，仅保留裁剪区域内的内容。</para>
        /// </remarks>
        /// <param name="canvas">SkiaSharp 画布</param>
        /// <param name="xleft">左列导联当前绘制 X 坐标</param>
        /// <param name="xmiddle">中列导联当前绘制 X 坐标</param>
        /// <param name="xright">右列导联当前绘制 X 坐标</param>
        /// <param name="xsec">次导联当前绘制 X 坐标</param>
        private void Erase(SKCanvas canvas, float xleft, float xmiddle,
            float xright, float xsec)
        {
            // 保存当前画布状态，以便后续恢复裁剪
            canvas.Save();

            // 构建"禁止绘制区域"路径: 包含导联头部 + 波形前沿擦除带
            // 裁剪后仅保留这些区域，重绘网格即可擦除旧波形痕迹
            using var builder = new SKPathBuilder();

            #region 禁止绘制导联头部区域(导联名称+定标方波所在区域)
            // 左列导联头部: 矩形(_oxs[0]-hwidth, 0, hwidth, 画布高)
            builder.AddRect(SKRect.Create(_oxs[0] - _hwidth, 0, _hwidth, _iheight));

            // 按导联模式添加其他列的导联头部
            switch (_mode)
            {
                case LeadMode.LM_6x2:
                    builder.AddRect(SKRect.Create(_oxs[2] - _hwidth, 0, _hwidth, _iheight));
                    break;
                case LeadMode.LM_6x2x1:
                    // 次导联模式: 右列导联头部高度为 6/7(次导联占底部 1/7)
                    builder.AddRect(SKRect.Create(_oxs[2] - _hwidth, 0, _hwidth, _iheight * 6 / 7));
                    break;
                case LeadMode.LM_6x3:
                    builder.AddRect(SKRect.Create(_oxs[1] - _hwidth, 0, _hwidth, _iheight));
                    builder.AddRect(SKRect.Create(_oxs[2] - _hwidth, 0, _hwidth, _iheight));
                    break;
                case LeadMode.LM_6x3x1:
                    builder.AddRect(SKRect.Create(_oxs[1] - _hwidth, 0, _hwidth, _iheight * 6 / 7));
                    builder.AddRect(SKRect.Create(_oxs[2] - _hwidth, 0, _hwidth, _iheight * 6 / 7));
                    break;
                case LeadMode.LM_3x4:
                    // 4 列模式(3x4): 第 2/3/4 列导联头部(整列高度，无次导联)
                    builder.AddRect(SKRect.Create(_oxs[1] - _hwidth, 0, _hwidth, _iheight));
                    builder.AddRect(SKRect.Create(_oxs[2] - _hwidth, 0, _hwidth, _iheight));
                    builder.AddRect(SKRect.Create(_oxs[3] - _hwidth, 0, _hwidth, _iheight));
                    break;
            }
            #endregion

            // ybottom: 主导联区域底部 Y 坐标 = 边距 + 6/7 或 6/6 的可用高度
            //          有次导联(x1 模式)时为 6/7(7行)，无次导联时为 6/6(全高)
            var ybottom = _ydelta / 2 + (_iheight - _ydelta) * 6 /
                ((_mode == LeadMode.LM_6x2 || _mode == LeadMode.LM_6x3 ||
                  _mode == LeadMode.LM_3x4 || _mode == LeadMode.LM_12x1) ? 6 : 7);

            #region 波形前沿擦除带(回绕时的擦除逻辑)
            // 当波形即将到达列右边界时，擦除带分为两段:
            //   1. 列尾残余部分(从当前位置到列右边界)
            //   2. 列头部分(从列起始开始的 _espace 宽度，为新波形让路)
            // 否则仅擦除当前位置前方 _espace 宽度的带状区域
            switch (_mode)
            {
                case LeadMode.LM_6x2:
                case LeadMode.LM_6x2x1:
                    if (xleft + _espace > _iwidth / 2)
                    {
                        var ll = SKRect.Create(_oxs[0] + _xedelta, 0, _espace - (_iwidth / 2 - xleft), ybottom);
                        var lr = SKRect.Create(xleft, 0, _iwidth / 2 - xleft, ybottom);
                        var rl = SKRect.Create(_oxs[2] + _xedelta, 0, _espace - (_iwidth - xright), ybottom);
                        var rr = SKRect.Create(xright, 0, _iwidth - xright - _xdelta / 2, ybottom);

                        builder.AddRect(ll);
                        builder.AddRect(lr);
                        builder.AddRect(rl);
                        builder.AddRect(rr);
                    }
                    else
                    {
                        var lr = SKRect.Create(xleft - _xedelta, 0, _espace, ybottom);
                        var rr = SKRect.Create(xright - _xedelta, 0, _espace, ybottom);

                        builder.AddRect(lr);
                        builder.AddRect(rr);
                    }

                    if (_mode == LeadMode.LM_6x2x1)
                    {
                        if (xsec + _espace > _iwidth - _xdelta / 2)
                        {
                            var ml = SKRect.Create(_oxs[0] + _xdelta / 2 - _xedelta, ybottom, _espace - (_iwidth - xsec), _iheight - ybottom);
                            var mr = SKRect.Create(xsec, ybottom,
                                _iwidth - xsec, _iheight - ybottom);

                            builder.AddRect(ml);
                            builder.AddRect(mr);
                        }
                        else
                        {
                            var mr = SKRect.Create(xsec, ybottom,
                                _espace, _iheight - ybottom);
                            builder.AddRect(mr);
                        }
                    }
                    break;

                case LeadMode.LM_6x3:
                case LeadMode.LM_6x3x1:
                    if (xleft + _espace > _iwidth / 3 + _xdelta / 6)
                    {
                        var ll = SKRect.Create(_oxs[0] + _xedelta, 0,
                            _espace - (_iwidth / 3 + _xdelta / 6 - xleft), ybottom);
                        var lr = SKRect.Create(xleft, 0,
                            _iwidth / 3 + _xdelta / 6 - xleft, ybottom);
                        var ml = SKRect.Create(_oxs[1] + _xedelta, 0,
                            _espace - (_iwidth / 3f - xleft), ybottom);
                        var mr = SKRect.Create(xmiddle, 0,
                            _iwidth / 3f - xleft, ybottom);
                        var rl = SKRect.Create(_oxs[2] + _xedelta, 0,
                            _espace - (_iwidth - xright), ybottom);
                        var rr = SKRect.Create(xright, 0,
                            _iwidth - xright - _xdelta / 2, ybottom);

                        builder.AddRect(ll);
                        builder.AddRect(lr);
                        builder.AddRect(ml);
                        builder.AddRect(mr);
                        builder.AddRect(rl);
                        builder.AddRect(rr);
                    }
                    else
                    {
                        var lr = SKRect.Create(xleft - _xedelta, 0, _espace, ybottom);
                        var mr = SKRect.Create(xmiddle - _xedelta, 0, _espace, ybottom);
                        var rr = SKRect.Create(xright - _xedelta, 0, _espace, ybottom);

                        builder.AddRect(lr);
                        builder.AddRect(mr);
                        builder.AddRect(rr);
                    }

                    if (_mode == LeadMode.LM_6x3x1)
                    {
                        if (xsec + _espace > _iwidth - _xdelta / 2)
                        {
                            var ml = SKRect.Create(_oxs[0] + _xdelta / 2 - _xedelta,
                                ybottom, _espace - (_iwidth - xsec), _iheight - ybottom);
                            var mr = SKRect.Create(xsec, ybottom, _iwidth - xsec,
                                _iheight - ybottom);

                            builder.AddRect(ml);
                            builder.AddRect(mr);
                        }
                        else
                        {
                            var mr = SKRect.Create(xsec, ybottom, _espace,
                                _iheight - ybottom);
                            builder.AddRect(mr);
                        }
                    }
                    break;

                case LeadMode.LM_3x4:
                    #region 4 列模式擦除带(列宽 = (画布宽-边距)/4)
                    {
                        // 列宽与第 0 列右边界
                        var colw = (_iwidth - _xdelta) / 4;
                        var colEnd = _xdelta / 2 + colw;

                        if (xleft + _espace > colEnd)
                        {
                            // 回绕时: 各列分两段擦除
                            //   1. 列尾残余(当前位置到列右边界)
                            //   2. 列头擦除带(列起始 + _espace 宽，为新波形让路)
                            var xs = new[] { xleft, xmiddle, xright, xsec };

                            for (var c = 0; c < 4; c++)
                            {
                                // 第 c 列右边界 = 左边距 + (c+1) × 列宽
                                var xend = _xdelta / 2 + (c + 1) * colw;

                                // 列尾残余段
                                builder.AddRect(SKRect.Create(xs[c], 0,
                                    xend - xs[c], ybottom));
                                // 列头擦除带
                                builder.AddRect(SKRect.Create(_oxs[c] + _xedelta,
                                    0, _espace, ybottom));
                            }
                        }
                        else
                        {
                            // 正常滚动: 仅擦除各列当前位置前方 _espace 宽的带状区域
                            builder.AddRect(SKRect.Create(xleft - _xedelta, 0,
                                _espace, ybottom));
                            builder.AddRect(SKRect.Create(xmiddle - _xedelta, 0,
                                _espace, ybottom));
                            builder.AddRect(SKRect.Create(xright - _xedelta, 0,
                                _espace, ybottom));
                            builder.AddRect(SKRect.Create(xsec - _xedelta, 0,
                                _espace, ybottom));
                        }
                    }
                    #endregion
                    break;

                default:
                    {
                        // 单列模式(LM_12x1/单导联): 列右边界 = 画布宽 - 右边距
                        var xend = _iwidth - _xdelta / 2;

                        if (xleft + _espace > xend)
                        {
                            // 回绕时: 列尾残余段 + 列头擦除带(与 DrawWaves 回绕条件匹配)
                            builder.AddRect(SKRect.Create(xleft, 0, xend - xleft, _iheight));
                            builder.AddRect(SKRect.Create(_oxs[0] + _xedelta, 0,
                                _espace - (xend - xleft), _iheight));
                        }
                        else
                        {
                            // 正常滚动: 仅擦除当前位置前方 _espace 宽的带状区域
                            builder.AddRect(SKRect.Create(xleft - _xedelta,
                                0, (float)_espace, _iheight));
                        }
                    }
                    break;
            }

            #endregion

            // 将构建好的路径作为裁剪区域: 仅保留"禁止绘制区域"可见
            using var path = builder.Detach();
            canvas.ClipPath(path, SKClipOperation.Intersect, true);

            // 在裁剪区域内重绘网格，实现擦除旧波形的效果
            // (网格覆盖旧波形，恢复干净的背景)
            DrawGrid(canvas);

            // 恢复裁剪前的画布状态(取消裁剪区域)
            canvas.Restore();
        }
        #endregion

        #region DrawDiagWaves：绘制离线生理电图波形
        /// <summary>
        /// 绘制离线诊断心电图波形。
        /// </summary>
        /// <remarks>
        /// <para>离线模式下整段数据一次性绘制到画布，支持水平滚动(delta 参数)和局部缩放(ZoomX/ZoomY)。</para>
        /// <para>绘制流程:</para>
        /// <list type="number">
        /// <item>绘制背景网格(DrawGrid)</item>
        /// <item>计算缩放后的步长和增益</item>
        /// <item>按导联模式确定可见导联数和每列窗口宽度(_xmaxpoints)</item>
        /// <item>遍历可见采样点，以 SKPathBuilder 构建各导联波形路径(MoveTo/LineTo)</item>
        /// <item>按导联索引用对应笔刷绘制路径</item>
        /// <item>若有次导联则单独绘制底部节律导联</item>
        /// <item>叠加手动标测卡尺(DrawCaliper)</item>
        /// </list>
        /// </remarks>
        /// <param name="canvas">SkiaSharp 画布</param>
        /// <param name="edata">波形数据，double[导联数][采样点数]，值为 ADC 原始值</param>
        /// <param name="delta">水平滚动增量(采样点数)，正值向右滚动看后面数据，负值向左</param>
        public void DrawDiagWaves(SKCanvas canvas, double[][] edata,
            int delta)
        {
            var data = edata?.Select(v => v.ToArray<double, float>()).ToList();
            DrawDiagWaves(canvas, data, delta);
        }
        /// <summary>
        /// 绘制离线生理电图波形（List&lt;List&lt;float&gt;&gt; 数据源重载）。
        /// </summary>
        /// <remarks>
        /// 该重载用于兼容二维 List 数据输入，内部会转换为 <see cref="List{T}"/>（元素类型为 <c>float[]</c>）
        /// 后复用主实现，确保所有离线绘制逻辑（滚动、缩放、卡尺叠加）保持一致。
        /// </remarks>
        /// <param name="canvas">画布</param>
        /// <param name="edata">生理电数据，外层为导联集合，内层为采样点序列</param>
        /// <param name="delta">水平滚动增量（采样点数），正值向后查看，负值向前查看</param>
        public void DrawDiagWaves(SKCanvas canvas, List<List<float>> edata,
            int delta)
        {
            var data = edata?.Select(v => v.ToArray()).ToList();
            DrawDiagWaves(canvas, data, delta);
        }

        /// <summary>
        /// 绘制离线生理电图波形（List&lt;float[]&gt; 主实现重载）。
        /// </summary>
        /// <remarks>
        /// 该方法是离线渲染主入口：先绘制网格，再按导联模式构建并绘制波形路径，
        /// 同时处理水平滚动、局部缩放与手动卡尺标测叠加。
        /// </remarks>
        /// <param name="canvas">画布</param>
        /// <param name="edata">生理电数据，按导联组织（<c>edata[导联索引][采样点索引]</c>）</param>
        /// <param name="delta">水平滚动增量（采样点数），用于浏览长时程波形</param>
        public void DrawDiagWaves(SKCanvas canvas, List<float[]> edata,
            int delta)
        {
            DrawGrid(canvas);

            // 应用局部缩放因子（ZoomX 水平放大，ZoomY 垂直放大）
            float zxstep = (float)(_xstep * ZoomX);
            double zgain = _gain * ZoomY;

            if (edata != null)
            {
                #region 画主要导联
                // 导联数
                var imin = _mode switch
                {
                    LeadMode.LM_6x2 or LeadMode.LM_6x2x1 or LeadMode.LM_3x4 or
                        LeadMode.LM_12x1 => 12,
                    LeadMode.LM_6x3 or LeadMode.LM_6x3x1 => 18,
                    _ => 1
                };

                _paths.Zero(imin).Reset();

                CorrectOffset(0);  // 修正主列滚动偏移(应用 delta 增量并限制边界)

                // 按导联模式计算各列可见窗口宽度(采样点数)
                // _xmaxpoints[0]: 主列每行可见采样点数; _xmaxpoints[1]: 次导联可见采样点数
                switch (_mode)
                {
                    case LeadMode.LM_6x2 or LeadMode.LM_6x2x1:
                        // 2 列模式: 每列宽度 = (画布宽/2 - 列起始偏移) / 步长
                        _xmaxpoints[0] = (int)((_iwidth / 2 - _oxs[0]) / zxstep);
                        _xmaxpoints[1] = (int)((_iwidth - _xdelta - _hwidth) / zxstep);
                        break;
                    case LeadMode.LM_6x3 or LeadMode.LM_6x3x1:
                        // 3 列模式: 每列宽度 = (画布宽/3 - 边距) / 步长
                        _xmaxpoints[0] = (int)((_iwidth / 3 - _xdelta / 3 - _hwidth) / zxstep);
                        _xmaxpoints[1] = (int)((_iwidth - _xdelta - _hwidth) / zxstep);
                        break;
                    case LeadMode.LM_3x4:
                        // 4 列模式: 每列宽度 = (画布宽/4 - 导联头) / 步长
                        _xmaxpoints[0] = (int)((_iwidth / 4 - _hwidth) / zxstep);
                        break;
                    default:
                        // LM_12x1 及单导联: 单列整宽 = (画布宽 - 边距 - 导联头) / 步长
                        _xmaxpoints[0] = (int)((_iwidth - _xdelta - _hwidth) / zxstep);
                        break;
                }

                // 各导联的路径构建器(SKPath.MoveTo/LineTo 在 SkiaSharp 4.x 已过时，改用 SKPathBuilder)
                var builders = new SKPathBuilder[imin];

                for (var j = 0; j < imin; j++)
                    builders[j] = new SKPathBuilder();

                var xstep = 0f;

                // 遍历可见采样点，构建各导联的波形路径
                for (int i = 0; i < _xmaxpoints[0]; i++)
                {
                    // 超出数据范围时停止(避免数组越界)
                    if (i + _offsets[0] >= edata[0].Length)
                        break;

                    // 当前采样点相对于列起始的 X 偏移 = 采样点序号 × 步长
                    var xstepi = i * zxstep;

                    // 为每个导联路径追加当前采样点的坐标
                    for (var j = 0; j < imin; j++)
                    {
                        // 单导联模式(imin==1): 数据源取实际导联索引(枚举值 0-17 与导联索引一致)；
                        // 若传入数据不足该索引(如仅 1 条)，回退到导联 0 避免越界
                        var lead = imin == 1 ? Math.Min((int)_mode, edata.Count - 1) : j;

                        // 根据导联模式确定 X 坐标: 不同列的导联使用不同的列基坐标 _oxs
                        var x = _mode switch
                        {
                            // 2 列模式: 导联 0-5 在左列(_oxs[0])，导联 6-11 在右列(_oxs[2])
                            LeadMode.LM_6x2 or LeadMode.LM_6x2x1 => j < 6 ?
                                _oxs[0] + xstepi : _oxs[2] + xstepi,
                            // 3 列模式: 导联 0-5 左列，6-11 中列(_oxs[1])，12-17 右列
                            LeadMode.LM_6x3 or LeadMode.LM_6x3x1 => j < 6 ?
                                _oxs[0] + xstepi : j < 12 ? _oxs[1] + xstepi : _oxs[2] + xstepi,
                            // 4 列模式(3x4 列优先): 列 = 导联/3(与 DrawHeaders 一致)
                            LeadMode.LM_3x4 => _oxs[j / 3] + xstepi,
                            // 单列模式: 所有导联共用 _oxs[0]
                            _ => _oxs[0] + xstepi
                        };

                        // Y 坐标 = 行基线 - ADC 值对应的像素偏移(正振幅向上)
                        // ToPixel: 将 ADC 原始值转换为像素
                        // Reset 已按导联索引存放行基线(含 LM_3x4 的行 i%3)，统一取 _ybases[j]
                        var y = _mode switch
                        {
                            LeadMode.LM_6x2 or LeadMode.LM_6x2x1 or LeadMode.LM_6x3 or
                            LeadMode.LM_6x3x1 or LeadMode.LM_12x1 or LeadMode.LM_3x4 =>
                                _ybases[j] - edata[lead][i + _offsets[0]].ToPixel(Dpm, zgain),
                            _ => _ybases[lead] - edata[lead][i + _offsets[0]].ToPixel(Dpm, zgain)
                        };

                        // 第一个点用 MoveTo(移动画笔不画线)，后续点用 LineTo(连线)
                        if (i == 0)
                            builders[j].MoveTo(x, y);
                        else
                            builders[j].LineTo(x, y);
                    }

                    xstep += zxstep;
                }

                // 构建完成: Detach 取出各导联路径替换复用的 _paths(释放旧路径避免泄漏)
                for (var j = 0; j < imin; j++)
                {
                    _paths[j].Dispose();
                    _paths[j] = builders[j].Detach();
                    builders[j].Dispose();
                }

                // 按导联索引导用对应笔刷（Uniform 模式下 GetWavePen 返回 _penwave）
                for (var j = 0; j < _paths.Count; j++)
                    canvas.DrawPath(_paths[j], GetWavePen(imin == 1 ? (int)_mode : j));
                #endregion

                #region 画次导联(节律导联，仅 LM_6x2x1 / LM_6x3x1 模式)
                if (_mode == LeadMode.LM_6x2x1 || _mode == LeadMode.LM_6x3x1)
                {
                    // 使用 SKPathBuilder 构建次导联路径(SKPath.MoveTo/LineTo 已过时)
                    using (var builder = new SKPathBuilder())
                    {
                        CorrectOffset(1);  // 修正次导联滚动偏移(独立于主列)

                        xstep = 0f;

                        // 遍历次导联可见采样点，构建波形路径
                        for (int i = 0; i < _xmaxpoints[1]; i++)
                        {
                            // 超出数据范围时停止
                            if (i + _offsets[1] >= edata[0].Length)
                                break;

                            // 次导联 X 坐标: 单列横跨整个画布宽度
                            var x = _oxs[0] + xstep;

                            // 次导联 Y 坐标: 基线 - 幅值(与其他导联及在线模式一致，正振幅向上)，
                            // 使用 _ybases[18](底部行)和 _secidx 指定的导联数据
                            var y = _ybases[18] - edata[_secidx][i + _offsets[1]].ToPixel(Dpm, zgain);

                            // 第一个点用 MoveTo(移动画笔不画线)，后续点用 LineTo(连线)
                            if (i == 0)
                                builder.MoveTo(x, y);
                            else
                                builder.LineTo(x, y);

                            xstep += zxstep;
                        }

                        // 用次导联对应的笔刷绘制路径(Detach 取出构建好的路径)
                        using (var path = builder.Detach())
                        {
                            canvas.DrawPath(path, GetWavePen(_secidx));
                        }
                    }
                }
                #endregion
            }
            else
                _log.Error("未加载诊断数据，请在调用 ScrollTo 之前调用 LoadDataPoints 加载诊断数据！");

            // 绘制手动标测卡尺（在波形绘制完成后叠加游标与测量标签）
            DrawCaliper(canvas, edata);

            // 局部函数: 修正滚动偏移量，应用 delta 增量并限制在有效范围内
            // index: 0=主列导联, 1=次导联
            void CorrectOffset(int index)
            {
                // 可见窗口宽度(采样点数): 当前模式下每行可显示的采样点数
                int xmax = _xmaxpoints[index];
                // 数据总长度: 取第一个导联的长度(所有导联长度一致)
                int len = edata[0].Length;
                // 最大偏移量: 数据末尾 - 可见窗口，确保最后一屏仍有数据可显示
                // 若数据总长 < 可见窗口(数据不足一屏)，则 maxOffset=0(从头开始)
                int maxOffset = len - xmax;
                if (maxOffset < 0) maxOffset = 0;

                // 应用滚动增量: 当前偏移 + delta(由鼠标滚轮/点击产生)
                int next = _offsets[index] + delta;
                // 边界限制: 不小于 0(数据开头)，不超过 maxOffset(数据末尾)
                if (next <= 0)
                    _offsets[index] = 0;        // 到达数据开头
                else if (next > maxOffset)
                    _offsets[index] = maxOffset; // 到达数据末尾
                else
                    _offsets[index] = next;     // 正常滚动
            }
        }
        #endregion

        #region DrawCaliper：绘制手动标测卡尺
        /// <summary>
        /// 绘制手动标测卡尺（双垂直游标虚线 + 两点连接线 + 测量数值标签）。
        /// <br/>在 <see cref="DrawDiagWaves"/> 末尾调用，仅当 <see cref="CaliperEnabled"/> 为 true
        /// 且已通过 <see cref="SetCaliper"/> 设置两点时绘制。
        /// </summary>
        /// <param name="canvas">画布</param>
        /// <param name="edata">波形数据（与 DrawDiagWaves 传入的同一份数据）</param>
        private void DrawCaliper(SKCanvas canvas, List<float[]> edata)
        {
            // 卡尺未启用或未设置两点时跳过
            if (!CaliperEnabled || _caliperStart == null || _caliperEnd == null || edata == null)
                return;

            var s = _caliperStart!;
            var e = _caliperEnd!;

            // 计算起点屏幕坐标（应用缩放因子）
            // 次导联点(LeadIndex==18): 列起点 _oxs[0](横跨整宽)、滚动偏移 _offsets[1]、数据源 _secidx
            // 主导联点: 按导联所在列取 _oxs[sCol]、偏移 _offsets[0]
            bool sSec = s.LeadIndex == 18;
            int sCol = GetLeadColumn(s.LeadIndex);
            float sx = (sSec ? _oxs[0] : _oxs[sCol]) +
                (s.DataIndex - (sSec ? _offsets[1] : _offsets[0])) * (float)(_xstep * ZoomX);
            int sData = sSec ? Math.Max(0, Math.Min(_secidx, edata.Count - 1)) : s.LeadIndex;
            int sIdx = Math.Max(0, Math.Min(s.DataIndex, edata[sData].Length - 1));
            // Y 与波形绘制一致取"基线 - 幅值"，使端点落在波形采样点上(正振幅向上)
            float sy = _ybases[YBaseIndex(s.LeadIndex)] - edata[sData][sIdx].ToPixel(Dpm, _gain * ZoomY);

            // 计算终点屏幕坐标(同起点逻辑)
            bool eSec = e.LeadIndex == 18;
            int eCol = GetLeadColumn(e.LeadIndex);
            float ex = (eSec ? _oxs[0] : _oxs[eCol]) +
                (e.DataIndex - (eSec ? _offsets[1] : _offsets[0])) * (float)(_xstep * ZoomX);
            int eData = eSec ? Math.Max(0, Math.Min(_secidx, edata.Count - 1)) : e.LeadIndex;
            int eIdx = Math.Max(0, Math.Min(e.DataIndex, edata[eData].Length - 1));
            // Y 与波形绘制一致取"基线 - 幅值"，使端点落在波形采样点上(正振幅向上)
            float ey = _ybases[YBaseIndex(e.LeadIndex)] - edata[eData][eIdx].ToPixel(Dpm, _gain * ZoomY);

            // 1. 绘制两条垂直游标虚线（贯穿画布可视区域）
            float top = Top;
            float bottom = top + ActualHeight;
            canvas.DrawLine(sx, top, sx, bottom, _pencaliper);
            canvas.DrawLine(ex, top, ex, bottom, _pencaliper);

            // 2. 绘制两点连接线（直观显示测量跨度）
            canvas.DrawLine(sx, sy, ex, ey, _pencaliper);

            // 3. 绘制测量值标签（时间间期 / 振幅差 / 瞬时心率）
            var (timeMs, ampMv, hrBpm) = GetMeasurement(edata);

            string label = $"Δt={timeMs:F0}ms  ΔV={ampMv:+0.00;-0.00;0.00}mV  HR={hrBpm:F0}bpm";

            // 标签位置：两点水平中点、垂直较高点上方
            float labelX = (sx + ex) / 2;
            float labelY = Math.Min(sy, ey) - _fontcaliper.Size - 2;

            // 边界保护：标签不超出画布顶部
            if (labelY < top + _fontcaliper.Size)
                labelY = top + _fontcaliper.Size + 2;

            // 使用临时实线笔刷绘制标签文本（卡尺笔刷为虚线，不适合文字）
            using var labelpaint = new SKPaint
            {
                Color = CaliperColor,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            canvas.DrawText(label, labelX, labelY, SKTextAlign.Center,
                _fontcaliper, labelpaint);
        }
        #endregion

        #region DrawRPeaks：在指定画布上为给定的 R 峰数组绘制竖直标记和顶部圆点
        /// <summary>
        /// 在指定画布上为给定的 R 峰数组绘制竖直标记和顶部圆点，并在前 12 个标记旁显示 RR 间期（毫秒）。
        /// </summary>
        /// <remarks>水平位置基于 Speed、Dpm、_drawfreq 与 ZoomX 计算；使用 Offsets 和 XmaxPoints
        /// 裁剪不可见点。竖线为半透明红，顶部为实心红点；仅前 12 个绘制项显示 RR 间期（以毫秒为单位）。</remarks>
        /// <param name="canvas">用于绘制的 SKCanvas 实例。</param>
        /// <param name="rpeaks">包含 R 峰索引的数组；仅对位于当前偏移和可见点范围内的索引绘制标记。</param>
        public void DrawRPeaks(SKCanvas canvas, IndexValue[] rpeaks)
        {
            float hwidth = 10f * Dpm;
            // 与 EPGRender 的 _xstep 保持一致：Speed × Dpm / drawfreq（诊断模式 drawfreq = Fs = 500）。
            // 再乘 ZoomX，使水平缩放时 R 峰标记与波形同步（EPGRender 用 zxstep = _xstep × ZoomX）。
            float xstep = (float)(Speed * Dpm / _drawfreq * ZoomX);

            float bottom = Top + ActualHeight;

            using var linePaint = new SKPaint
            {
                Color = new SKColor(220, 50, 50, 70),   // 半透明红，不严重遮挡波形
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };

            using var dotPaint = new SKPaint
            {
                Color = new SKColor(220, 50, 50, 240),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            using var txtPaint = new SKPaint
            {
                Color = new SKColor(220, 50, 50, 240),
                IsAntialias = true
            };

            using var txtFont = new SKFont
            {
                Size = 16,
                Typeface = SKTypeface.FromFamilyName("Consolas")
            };

            int drawn = 0;

            foreach (var rp in rpeaks)
            {
                int idx = rp.Index - Offsets[0];

                if (idx < 0 || idx >= XmaxPoints[0]) continue;

                float x = Left + hwidth + idx * xstep;

                // 贯穿竖线（淡红）
                canvas.DrawLine(x, Top, x, bottom, linePaint);
                // 顶部圆点
                canvas.DrawCircle(x, Top + 5, 3.5f, dotPaint);

                // RR 间期标注（前 12 个 R 峰标，避免过密）
                if (drawn < 12 && drawn > 0)
                {
                    var prev = rpeaks[drawn - 1];

                    double rrms = (rp.Index - prev.Index) * 1000.0 / _drawfreq;

                    canvas.DrawText($"{rrms:F0}", x + 4, Top + 16, SKTextAlign.Left, txtFont, txtPaint);
                }

                drawn++;
            }
        }
        #endregion

        #region Print：打印心电图报告单
        /// <summary>
        /// 打印心电图报告单为 PDF 文件。
        /// </summary>
        /// <remarks>
        /// <para>按指定的纸张尺寸(A4: 210×297mm)生成多页 PDF，每页包含:</para>
        /// <list type="bullet">
        /// <item>标题区: 报告名称(如"心电图报告单") + 二维码</item>
        /// <item>波形区: 按导联模式布局绘制波形(每页显示 XmaxPoints[0] 个采样点)</item>
        /// <item>页码: 底部居中显示页码</item>
        /// </list>
        /// <para>非 Pdf 类型(如 Xps)当前未实现，抛出 NotSupportedException。</para>
        /// </remarks>
        /// <param name="path">输出 PDF 文件路径</param>
        /// <param name="edata">波形数据，float[导联数][采样点数]</param>
        /// <param name="iwidth">纸张宽度(mm)，默认 210(A4)</param>
        /// <param name="iheight">纸张高度(mm)，默认 297(A4)</param>
        /// <param name="dpi">打印分辨率(像素/英寸)，默认 300(打印标准清晰度，96 为屏幕级会发虚)</param>
        /// <param name="title">报告标题文字</param>
        /// <param name="qrc">二维码内容字符串（Single 版面用作条码内容，空白时回退 patientId）</param>
        /// <param name="mode">导联布局模式</param>
        /// <param name="type">打印类型(当前仅支持 Pdf)</param>
        /// <param name="layout">报告版面；<see cref="EcgReportLayout.None"/> 为既有纯波形图纸行为</param>
        /// <param name="patientName">患者姓名</param>
        /// <param name="patientId">患者 ID / 住院号</param>
        /// <param name="patientAge">年龄（数值字符串，如 "45"）</param>
        /// <param name="patientGender">性别（"男" / "女"）</param>
        /// <param name="bedNo">床号</param>
        /// <param name="department">申请科室</param>
        /// <param name="hospital">医院名称</param>
        /// <param name="doctor">申请 / 报告医生</param>
        /// <param name="auditor">审核医生</param>
        /// <param name="device">设备名称</param>
        /// <param name="filter">滤波设置</param>
        /// <param name="heartRate">心率(bpm)</param>
        /// <param name="rr">RR 间期(ms)</param>
        /// <param name="pr">PR 间期(ms)</param>
        /// <param name="qrs">QRS 时限(ms)</param>
        /// <param name="qt">QT 间期(ms)</param>
        /// <param name="qtc">QTc 间期(ms)</param>
        /// <param name="axisP">P 电轴(°)</param>
        /// <param name="axisQRS">QRS 电轴(°)</param>
        /// <param name="axisT">T 电轴(°)</param>
        /// <param name="diagnoses">VH 诊断码（长文本）列表</param>
        /// <param name="mcCodes">明尼苏达码列表</param>
        /// <param name="impression">分析意见（可含换行）</param>
        /// <param name="critical">危急值文本（null / 空白表示无危急值）</param>
        /// <param name="sampleRate">采样率(Hz)，用于报告版面的 drawfreq；≤0 时回退 500</param>
        /// <param name="reportDate">检查 / 报告时间；null 时取 <see cref="DateTime.Now"/></param>
        public void Print(string path, List<float[]> edata,
            int iwidth = 210, int iheight = 297, int dpi = 300,
            string title = "心电图报告单", string qrc = "ECG",
            LeadMode mode = LeadMode.LM_6x2,
            SkiaPrintType type = SkiaPrintType.Pdf,
            EcgReportLayout layout = EcgReportLayout.None,
            string patientName = null, string patientId = null, string patientAge = null,
            string patientGender = null, string bedNo = null, string department = null,
            string hospital = null, string doctor = null, string auditor = null,
            string device = null, string filter = null,
            int heartRate = 0, int rr = 0, int pr = 0, int qrs = 0, int qt = 0, int qtc = 0,
            int axisP = 0, int axisQRS = 0, int axisT = 0,
            IEnumerable<string> diagnoses = null, IEnumerable<string> mcCodes = null,
            string impression = null, string critical = null,
            int sampleRate = 0, DateTime? reportDate = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            if (edata == null || edata.Count == 0 || edata[0] == null || edata[0].Length == 0)
                throw new ArgumentException("心电图数据不能为空。", nameof(edata));

            if (File.Exists(path))
                File.Delete(path);

            if (layout == EcgReportLayout.None)
            {
                // 保持原有无布局模式的兼容性：直接调用文档输出
                // 若需要完全按示例图片档案版式，需使用 Single
                layout = EcgReportLayout.Single;
            }

            int effectiveSampleRate = sampleRate > 0 ? sampleRate : 500;
            var reportTime = reportDate ?? DateTime.Now;

            switch (layout)
            {
                case EcgReportLayout.Single:
                    {
                        if (type != SkiaPrintType.Pdf)
                            throw new NotSupportedException("当前仅支持 PDF 输出。");

                        var metadata = new SKDocumentPdfMetadata
                        {
                            Author = "Twins",
                            Creator = "Twins",
                            Producer = "Twins",
                            Subject = "ECG Report",
                            Title = title ?? "心电图报告单",
                            Creation = reportTime,
                            Modified = reportTime,
                            RasterDpi = dpi
                        };

                        using var document = SKDocument.CreatePdf(path, metadata);

                        if (document == null)
                            throw new InvalidOperationException("无法创建 PDF 文档。");

                        float mmToPx = dpi / 25.4f;

                        float pageWidth = iwidth * mmToPx;
                        float pageHeight = iheight * mmToPx;

                        float Mm(float v) => v * mmToPx;

                        using var borderPaint = new SKPaint
                        {
                            Color = SKColors.Black,
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = Math.Max(0.18f * mmToPx, 0.8f),
                            IsAntialias = true
                        };

                        using var textPaint = new SKPaint
                        {
                            Color = SKColors.Black,
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true
                        };

                        using var redPaint = new SKPaint
                        {
                            Color = new SKColor(180, 0, 0),
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true
                        };

                        using var titleFont = new SKFont
                        {
                            Typeface = _zhcn,
                            Size = Mm(5.8f)
                        };

                        using var headerFont = new SKFont
                        {
                            Typeface = _zhcn,
                            Size = Mm(3.0f)
                        };

                        using var normalFont = new SKFont
                        {
                            Typeface = _zhcn,
                            Size = Mm(2.7f)
                        };

                        using var smallFont = new SKFont
                        {
                            Typeface = _zhcn,
                            Size = Mm(2.25f)
                        };

                        using var monoFont = new SKFont
                        {
                            Typeface = _usen,
                            Size = Mm(2.45f)
                        };

                        const float frameLeftMm = 3f;
                        const float frameTopMm = 3f;
                        const float frameWidthMm = 204f;
                        const float frameHeightMm = 291f;

                        const float titleTopMm = 4f;
                        const float titleHeightMm = 19f;
                        const float patientTopMm = 30f;
                        const float patientHeightMm = 16f;
                        const float waveTopMm = 47f;
                        const float waveHeightMm = 180f;
                        const float resultTopMm = 229f;
                        const float resultHeightMm = 54f;

                        float waveWidthPx = Mm(frameWidthMm);
                        float waveHeightPx = Mm(waveHeightMm);

                        using var render = new EPGRender(mmToPx, (int)Math.Round(Mm(2.8f)))
                        {
                            IsPrinting = true,
                            Speed = Speed,
                            Gain = Gain,
                            ShowCalibration = ShowCalibration,
                            LeadColorMode = LeadColorMode,
                            LeadGroupColors = LeadGroupColors
                        };

                        render.Theme.Background = SKColors.White;
                        render.Theme.ThinGridColor = new SKColor(180, 180, 180);
                        render.Theme.ThickGridColor = new SKColor(105, 105, 105);
                        render.Theme.WaveColor = new SKColor(25, 25, 25);
                        render.Theme.HeaderColor = new SKColor(25, 25, 25);
                        render.Theme.ThinGridStrokeWidth = Math.Max(Mm(0.06f), 0.5f);
                        render.Theme.ThickGridStrokeWidth = Math.Max(Mm(0.14f), 0.8f);
                        render.Theme.WaveStrokeWidth = Math.Max(Mm(0.18f), 1f);
                        render.Theme.HeaderStrokeWidth = Math.Max(Mm(0.16f), 1f);
                        render.ApplyTheme();

                        render.Reset(
                            waveWidthPx,
                            waveHeightPx,
                            mode,
                            _secidx,
                            effectiveSampleRate);

                        int page = 1;
                        int targetOffset = 0;

                        while (targetOffset < edata[0].Length)
                        {
                            using var canvas = document.BeginPage(pageWidth, pageHeight);

                            canvas.Clear(SKColors.White);

                            canvas.DrawRect(
                                Mm(frameLeftMm),
                                Mm(frameTopMm),
                                Mm(frameWidthMm),
                                Mm(frameHeightMm),
                                borderPaint);

                            canvas.DrawRect(
                                Mm(frameLeftMm),
                                Mm(titleTopMm),
                                Mm(frameWidthMm),
                                Mm(titleHeightMm),
                                borderPaint);

                            DrawCode128Bar(
                                canvas,
                                string.IsNullOrWhiteSpace(qrc) ? (patientId ?? "ECG") : qrc,
                                Mm(3.8f),
                                Mm(8.2f),
                                Mm(29f),
                                Mm(6.5f),
                                textPaint);

                            string qrPayload = BuildQrPayload(patientId, patientName, reportTime, qrc);

                            //DrawQrCode(
                            //    canvas,
                            //    qrPayload,
                            //    SKRect.Create(Mm(188f), Mm(5f), Mm(15.5f), Mm(15.5f)));

                            canvas.DrawText(
                                string.IsNullOrWhiteSpace(title) ? "心电图报告单" : title,
                                Mm(108f),
                                Mm(16.5f),
                                SKTextAlign.Center,
                                titleFont,
                                textPaint);

                            if (!string.IsNullOrWhiteSpace(hospital))
                            {
                                canvas.DrawText(
                                    hospital,
                                    Mm(29f),
                                    Mm(16.5f),
                                    SKTextAlign.Left,
                                    headerFont,
                                    textPaint);
                            }

                            canvas.DrawText(
                                $"检查时间：{reportTime:yyyy-MM-dd HH:mm:ss}    报告时间：{reportTime:yyyy-MM-dd HH:mm:ss}",
                                Mm(205f),
                                Mm(27.5f),
                                SKTextAlign.Right,
                                normalFont,
                                textPaint);

                            DrawPatientInfoTable(
                                canvas,
                                Mm(frameLeftMm),
                                Mm(patientTopMm),
                                Mm(frameWidthMm),
                                Mm(patientHeightMm),
                                patientName,
                                patientGender,
                                patientAge,
                                doctor,
                                department,
                                patientId,
                                bedNo,
                                reportTime,
                                borderPaint,
                                textPaint,
                                normalFont);

                            canvas.DrawRect(
                                Mm(frameLeftMm),
                                Mm(waveTopMm),
                                Mm(frameWidthMm),
                                Mm(waveHeightMm),
                                borderPaint);

                            canvas.DrawText(
                                $"{Speed}mm/s   {Gain}mm/mV   {(string.IsNullOrWhiteSpace(filter) ? "Notch: 50Hz   0.05Hz-35Hz" : filter)}",
                                Mm(205f),
                                Mm(waveTopMm + 4.2f),
                                SKTextAlign.Right,
                                smallFont,
                                textPaint);

                            canvas.Save();
                            canvas.Translate(Mm(frameLeftMm), Mm(waveTopMm));

                            int delta = targetOffset - render.Offsets[0];
                            render.DrawDiagWaves(canvas, edata, delta);

                            canvas.Restore();

                            int pointsPerPage = Math.Max(1, render.XmaxPoints[0]);

                            DrawImageStyleReportFooter(
                                canvas,
                                Mm(frameLeftMm),
                                Mm(resultTopMm),
                                Mm(frameWidthMm),
                                Mm(resultHeightMm),
                                (float v) => v * mmToPx,
                                heartRate,
                                rr,
                                pr,
                                qrs,
                                qt,
                                qtc,
                                axisP,
                                axisQRS,
                                axisT,
                                diagnoses?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>(),
                                mcCodes?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>(),
                                impression,
                                critical,
                                auditor,
                                device,
                                reportTime,
                                page,
                                borderPaint,
                                textPaint,
                                redPaint,
                                normalFont,
                                smallFont,
                                monoFont);

                            document.EndPage();

                            page++;
                            targetOffset += pointsPerPage;
                        }

                        document.Close();
                    }
                    break;

                default:
                    throw new NotSupportedException($"不支持的报告布局: {layout}");
            }
        }
        #endregion

        #region 报告辅助
        private static string BuildQrPayload(string patientId, string patientName, DateTime reportTime, string reportId)
        {
            string Escape(string value) =>
                Uri.EscapeDataString(value ?? string.Empty);

            return string.Join("|", new[]
            {
                "ECG",
                $"RID={Escape(reportId)}",
                $"PID={Escape(patientId)}",
                $"PN={Escape(patientName)}",
                $"TIME={reportTime:yyyyMMddHHmmss}"
            });
        }

        private static void DrawPatientInfoTable(
            SKCanvas canvas,
            float x,
            float y,
            float width,
            float height,
            string patientName,
            string patientGender,
            string patientAge,
            string doctor,
            string department,
            string patientId,
            string bedNo,
            DateTime reportTime,
            SKPaint borderPaint,
            SKPaint textPaint,
            SKFont font)
        {
            canvas.DrawRect(x, y, width, height, borderPaint);

            float rowHeight = height / 2f;
            float firstRowY = y + rowHeight;

            canvas.DrawLine(x, firstRowY, x + width, firstRowY, borderPaint);

            float[] firstRowWidths =
            {
                width * 0.16f,
                width * 0.12f,
                width * 0.14f,
                width * 0.25f,
                width * 0.33f
            };

            float currentX = x;
            for (int i = 0; i < firstRowWidths.Length - 1; i++)
            {
                currentX += firstRowWidths[i];
                canvas.DrawLine(currentX, y, currentX, y + rowHeight, borderPaint);
            }

            float secondSplit1 = x + width * 0.45f;
            float secondSplit2 = x + width * 0.60f;

            canvas.DrawLine(secondSplit1, firstRowY, secondSplit1, y + height, borderPaint);
            canvas.DrawLine(secondSplit2, firstRowY, secondSplit2, y + height, borderPaint);

            float baseline1 = y + rowHeight * 0.68f;
            float baseline2 = firstRowY + rowHeight * 0.68f;
            float pad = height * 0.12f;

            canvas.DrawText($"姓名：{patientName ?? string.Empty}", x + pad, baseline1, SKTextAlign.Left, font, textPaint);
            canvas.DrawText($"性别：{patientGender ?? string.Empty}", x + firstRowWidths[0] + pad, baseline1, SKTextAlign.Left, font, textPaint);
            canvas.DrawText($"年龄：{patientAge ?? string.Empty} 岁", x + firstRowWidths[0] + firstRowWidths[1] + pad, baseline1, SKTextAlign.Left, font, textPaint);
            canvas.DrawText($"申请医生：{doctor ?? string.Empty}", x + firstRowWidths[0] + firstRowWidths[1] + firstRowWidths[2] + pad, baseline1, SKTextAlign.Left, font, textPaint);
            canvas.DrawText($"申请科室：{department ?? string.Empty}", x + firstRowWidths[0] + firstRowWidths[1] + firstRowWidths[2] + firstRowWidths[3] + pad, baseline1, SKTextAlign.Left, font, textPaint);

            canvas.DrawText($"住院号：{patientId ?? string.Empty}", x + pad, baseline2, SKTextAlign.Left, font, textPaint);
            canvas.DrawText($"床号：{bedNo ?? string.Empty}", secondSplit1 + pad, baseline2, SKTextAlign.Left, font, textPaint);
            canvas.DrawText($"报告日期：{reportTime:yyyy-MM-dd}", secondSplit2 + pad, baseline2, SKTextAlign.Left, font, textPaint);
        }

        private static void DrawImageStyleReportFooter(
            SKCanvas canvas,
            float x,
            float y,
            float width,
            float height,
            Func<float, float> mm,
            int heartRate,
            int rr,
            int pr,
            int qrs,
            int qt,
            int qtc,
            int axisP,
            int axisQRS,
            int axisT,
            IList<string> diagnoses,
            IList<string> mcCodes,
            string impression,
            string critical,
            string auditor,
            string device,
            DateTime printTime,
            int page,
            SKPaint borderPaint,
            SKPaint textPaint,
            SKPaint redPaint,
            SKFont normalFont,
            SKFont smallFont,
            SKFont monoFont)
        {
            canvas.DrawRect(x, y, width, height, borderPaint);

            float metricWidth = width * 0.17f;
            float diagnosisX = x + metricWidth + mm(2f);
            float diagnosisWidth = width - metricWidth - mm(4f);

            canvas.DrawLine(
                x + metricWidth,
                y,
                x + metricWidth,
                y + height,
                borderPaint);

            float metricX = x + mm(1.2f);
            float metricY = y + mm(5f);
            float metricLineHeight = mm(3.8f);

            void DrawMetric(string value)
            {
                canvas.DrawText(value, metricX, metricY, SKTextAlign.Left, normalFont, textPaint);
                metricY += metricLineHeight;
            }

            DrawMetric($"心率：{heartRate} bpm");
            DrawMetric($"RR间期：{rr} ms");
            DrawMetric($"QRS时限：{qrs} ms");
            DrawMetric($"PR间期：{pr} ms");
            DrawMetric($"QT/QTc：{qt}/{qtc} ms");
            DrawMetric($"P/QRS/T：{axisP}°/{axisQRS}°/{axisT}°");
            if (!string.IsNullOrWhiteSpace(device))
                DrawMetric($"设备：{device}");

            float textY = y + mm(5f);

            canvas.DrawText("诊断结果：", diagnosisX, textY, SKTextAlign.Left, normalFont, textPaint);

            textY += mm(4.5f);

            if (diagnoses != null)
            {
                foreach (var diagnosis in diagnoses)
                {
                    DrawReportWrappedText(canvas, $"• {diagnosis}", diagnosisX, ref textY, diagnosisWidth, normalFont, textPaint, mm(3.8f));
                }
            }

            if (mcCodes != null && mcCodes.Count > 0)
            {
                DrawReportWrappedText(canvas, $"明尼苏达码：{string.Join("，", mcCodes)}", diagnosisX, ref textY, diagnosisWidth, smallFont, textPaint, mm(3.5f));
            }

            if (!string.IsNullOrWhiteSpace(impression))
            {
                textY += mm(1.5f);
                canvas.DrawText("分析意见：", diagnosisX, textY, SKTextAlign.Left, normalFont, textPaint);
                textY += mm(4f);

                foreach (var line in impression.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    DrawReportWrappedText(canvas, line, diagnosisX, ref textY, diagnosisWidth, smallFont, textPaint, mm(3.5f));
                }
            }

            if (!string.IsNullOrWhiteSpace(critical))
            {
                textY += mm(1f);
                DrawReportWrappedText(canvas, $"危急值：{critical}", diagnosisX, ref textY, diagnosisWidth, normalFont, redPaint, mm(3.8f));
            }

            float signY = y + height - mm(11f);

            canvas.DrawText($"审核医生签名：{auditor ?? string.Empty}", x + width - mm(62f), signY, SKTextAlign.Left, normalFont, textPaint);
            canvas.DrawLine(x + width - mm(38f), signY + mm(1.2f), x + width - mm(3f), signY + mm(1.2f), borderPaint);
            canvas.DrawText($"打印时间：{printTime:yyyy-MM-dd HH:mm:ss}    第 {page} 页", x + width - mm(3f), y + height - mm(3f), SKTextAlign.Right, smallFont, textPaint);
            canvas.DrawText("注：所有参数和结论须经医生最终签名确认，本报告仅供临床参考。", x + mm(1.2f), y + height - mm(3f), SKTextAlign.Left, smallFont, textPaint);
        }

        private static void DrawReportWrappedText(SKCanvas canvas, string text, float x, ref float y, float maxWidth, SKFont font, SKPaint paint, float lineHeight)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string line = string.Empty;

            foreach (char c in text)
            {
                string candidate = line + c;
                if (line.Length > 0 && font.MeasureText(candidate) > maxWidth)
                {
                    canvas.DrawText(line, x, y, SKTextAlign.Left, font, paint);
                    y += lineHeight;
                    line = c.ToString();
                }
                else
                {
                    line = candidate;
                }
            }

            if (!string.IsNullOrEmpty(line))
            {
                canvas.DrawText(line, x, y, SKTextAlign.Left, font, paint);
                y += lineHeight;
            }
        }

        private static void DrawCode128Bar(SKCanvas canvas, string content, float x, float y, float width, float height, SKPaint paint)
        {
            if (string.IsNullOrWhiteSpace(content))
                content = "ECG";

            var bars = new List<int>() { 2, 1, 1, 1, 2, 1 };

            foreach (char c in content)
            {
                int value = c;
                bars.Add(1 + (value & 0x01));
                bars.Add(1);
                bars.Add(1 + ((value >> 1) & 0x01));
                bars.Add(1 + ((value >> 2) & 0x01));
                bars.Add(1);
                bars.Add(1 + ((value >> 3) & 0x01));
            }

            bars.AddRange(new[] { 2, 1, 1, 1, 2 });

            float unitWidth = width / bars.Sum();
            float currentX = x;
            bool draw = true;

            foreach (var unit in bars)
            {
                float w = unit * unitWidth;

                if (draw)
                    canvas.DrawRect(currentX, y, w, height, paint);

                currentX += w;
                draw = !draw;
            }
        }

        private static void DrawQrCode(SKCanvas canvas, string payload, SKRect rect)
        {
            if (canvas == null)
                throw new ArgumentNullException(nameof(canvas));

            if (string.IsNullOrWhiteSpace(payload))
                return;

            int size = Math.Max(1, (int)Math.Ceiling(Math.Min(rect.Width, rect.Height)));

            using var image = payload.ToQrCode(size, new SKPaint
            {
                Color = SKColors.Black,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = false
            });

            using var bg = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Fill,
                IsAntialias = false
            };

            canvas.DrawRect(rect, bg);

            using var imgPaint = new SKPaint
            {
                //FilterQuality = SKFilterQuality.None,
                IsAntialias = false
            };

            canvas.DrawImage(image, rect, imgPaint);
        }


        #endregion

        #region Dispose：释放所有 SkiaSharp 资源(笔刷、字体、字体文件、路径)
        /// <summary>
        /// 释放所有 SkiaSharp 资源(笔刷、字体、字体文件、路径)。
        /// <br/>调用后不可再使用此实例的任何绘制方法。
        /// </summary>
        public void Dispose()
        {
            // 释放网格笔刷
            _pen1mm?.Dispose();
            _pen5mm?.Dispose();
            _penwave?.Dispose();

            // 释放按导联分组的笔刷数组
            if (_leadPens != null)
            {
                foreach (var pen in _leadPens)
                    pen?.Dispose();

                _leadPens = null;
            }

            _fontheader?.Dispose();
            _penbg?.Dispose();
            _zhcn?.Dispose();
            _paths?.Dispose();
            _penheader?.Dispose();
            _pencaliper?.Dispose();
            _fontcaliper?.Dispose();
            _usen?.Dispose();
        }
        #endregion
    }
}