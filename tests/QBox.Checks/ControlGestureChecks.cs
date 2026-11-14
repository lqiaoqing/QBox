using QBox.Core.Input;
using static QBox.Checks.CheckSuite;

namespace QBox.Checks;

internal static class ControlGestureChecks
{
    public static void Run(CheckSuite checks)
    {
        static bool Tap(DoubleControlDetector detector, uint key, uint start, uint duration = 20)
        { True(!detector.OnKey(key, true, start)); return detector.OnKey(key, false, unchecked(start + duration)); }
        foreach (var key in new uint[] { 0xa2, 0xa3, 0x11 })
            checks.Run($"双击 Ctrl {key:x} 只在第二次松开后触发", () =>
            {
                var detector = new DoubleControlDetector();
                True(!Tap(detector, key, 10));
                True(Tap(detector, key, 100));
                True(!Tap(detector, key, 200));
            });
        checks.Run("Ctrl 组合键清除前一次单击", () =>
        {
            var detector = new DoubleControlDetector();
            True(!Tap(detector, 0xa2, 10));
            True(!detector.OnKey(0xa2, true, 50));
            True(!detector.OnKey(0x43, true, 60));
            True(!detector.OnKey(0x43, false, 70));
            True(!detector.OnKey(0xa2, false, 80));
            True(!Tap(detector, 0xa2, 100));
            True(Tap(detector, 0xa2, 150));
        });
        checks.Run("长按 Ctrl 和超时双击不会唤起", () =>
        {
            var detector = new DoubleControlDetector();
            True(!Tap(detector, 0xa2, 10, 500));
            True(!Tap(detector, 0xa2, 600));
            True(!Tap(detector, 0xa2, 1200));
            True(Tap(detector, 0xa2, 1300));
        });
        checks.Run("Ctrl 自动重复不计作多次点击", () =>
        {
            var detector = new DoubleControlDetector();
            True(!detector.OnKey(0xa2, true, 10));
            True(!detector.OnKey(0xa2, true, 20));
            True(!detector.OnKey(0xa2, true, 30));
            True(!detector.OnKey(0xa2, false, 40));
            True(Tap(detector, 0xa2, 100));
        });
        checks.Run("Shift Alt Win 修饰键不会误触双击 Ctrl", () =>
        {
            foreach (var modifier in new uint[] { 0xa0, 0xa1, 0xa4, 0xa5, 0x5b, 0x5c })
            {
                var detector = new DoubleControlDetector();
                True(!detector.OnKey(modifier, true, 0));
                True(!Tap(detector, 0xa2, 10));
                True(!Tap(detector, 0xa2, 60));
                True(!detector.OnKey(modifier, false, 90));
                True(!Tap(detector, 0xa2, 100));
                True(Tap(detector, 0xa2, 150));
            }
        });
        checks.Run("左右 Ctrl 重叠按下不算双击", () =>
        {
            var detector = new DoubleControlDetector();
            True(!detector.OnKey(0xa2, true, 0));
            True(!detector.OnKey(0xa3, true, 10));
            True(!detector.OnKey(0xa2, false, 20));
            True(!detector.OnKey(0xa3, false, 30));
            True(!Tap(detector, 0xa2, 50));
        });
        checks.Run("系统计时回绕仍可识别双击 Ctrl", () =>
        {
            var detector = new DoubleControlDetector();
            True(!Tap(detector, 0xa2, uint.MaxValue - 100));
            True(Tap(detector, 0xa2, 20));
        });
    }
}
