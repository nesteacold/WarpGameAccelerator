using Microsoft.UI.Dispatching;

namespace WarpGameAccelerator.Services;

/// <summary>Bơm thật cho production: giao công việc xuống hàng đợi UI đúng như cách app
/// vẫn dùng (`MainWindow.xaml.cs:248` — `DispatcherQueue.TryEnqueue`, đã chạy thật).</summary>
public sealed class DispatcherLivenessPump(DispatcherQueue queue) : IUiLivenessPump
{
    public bool TryPost(Action work) => queue.TryEnqueue(() => work());
}
