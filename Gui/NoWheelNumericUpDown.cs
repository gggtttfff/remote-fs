namespace RemoteFs.Gui;

/// <summary>
/// 忽略鼠标滚轮改值的数字框：滚轮交给外层可滚动容器，点击箭头与键盘仍可调值。
/// 作者：Pieory，日期：2025-07-18
/// </summary>
internal sealed class NoWheelNumericUpDown : NumericUpDown
{
    /// <summary>
    /// 不调用基类，避免 UpDownBase 按滚轮增减 Value。
    /// 保持 Handled=false，让 WM_MOUSEWHEEL 冒泡到外层 Panel/Form，从而滚动页面。
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = false;
        }
    }
}
