namespace QBox.Core.Input;

public sealed class DoubleControlDetector(uint interval = 400, uint maximumPress = 300)
{
    private int _controls, _modifiers;
    private bool _chord, _pending;
    private uint _pressedAt, _releasedAt;

    public bool OnKey(uint virtualKey, bool down, uint time)
    {
        var control = virtualKey switch { 0x11 or 0xa2 => 1, 0xa3 => 2, _ => 0 };
        if (control == 0)
        {
            var modifier = virtualKey switch
            {
                0xa0 => 1, 0xa1 => 2, 0xa4 => 4, 0xa5 => 8,
                0x5b => 16, 0x5c => 32, 0x10 => 64, 0x12 => 128, _ => 0
            };
            if (down)
            {
                _modifiers |= modifier;
                _pending = false;
                if (_controls != 0) _chord = true;
            }
            else _modifiers &= ~modifier;
            return false;
        }
        if (down)
        {
            if ((_controls & control) != 0) return false;
            if (_controls == 0) { _pressedAt = time; _chord = _modifiers != 0; }
            else _chord = true;
            _controls |= control;
            return false;
        }
        if ((_controls & control) == 0) { _pending = false; return false; }
        _controls &= ~control;
        if (_controls != 0 || _chord || unchecked(time - _pressedAt) > maximumPress)
        {
            _pending = false;
            return false;
        }
        // 使用无符号差值兼容系统计时器回绕；松开第二次 Ctrl 后才确认，避免把 Ctrl+C 当成双击。
        if (_pending && unchecked(time - _releasedAt) <= interval)
        {
            _pending = false;
            return true;
        }
        _pending = true;
        _releasedAt = time;
        return false;
    }
}
