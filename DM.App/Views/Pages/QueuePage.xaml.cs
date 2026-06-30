using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DM.App.ViewModels;
using DM.Core.Queue;
using MouseEventArgs  = System.Windows.Input.MouseEventArgs;
using DragEventArgs   = System.Windows.DragEventArgs;
using DataObject      = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;

namespace DM.App.Views.Pages;

public partial class QueuePage : Page
{
    private readonly DownloadQueueViewModel _vm;
    private QueueItemViewModel? _dragSource;
    private Point _dragOrigin;

    public QueuePage(DownloadQueueViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    // ── Drag-to-reorder ────────────────────────────────────────────────────

    private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is QueueItemViewModel item)
        {
            _dragSource = item;
            _dragOrigin = e.GetPosition(null);
        }
    }

    private void QueueItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = new DataObject(typeof(QueueItemViewModel), _dragSource);
        DragDrop.DoDragDrop(QueueList, data, DragDropEffects.Move);
        _dragSource = null;
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(QueueItemViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(QueueItemViewModel))) return;
        var dragged = (QueueItemViewModel)e.Data.GetData(typeof(QueueItemViewModel));

        var target = FindDropTarget(e.GetPosition(QueueList));
        if (target is null || target.Entry.Id == dragged.Entry.Id) return;

        int srcIdx    = _vm.Items.IndexOf(dragged);
        int targetIdx = _vm.Items.IndexOf(target);
        if (srcIdx < 0 || targetIdx < 0) return;

        // Step the item one slot at a time so MoveUp/MoveDown stays in sync
        // with the queue manager's sort indices.
        while (srcIdx > targetIdx) { _vm.MoveUpCommand.Execute(dragged);   srcIdx--; }
        while (srcIdx < targetIdx) { _vm.MoveDownCommand.Execute(dragged); srcIdx++; }
    }

    private QueueItemViewModel? FindDropTarget(Point relativeToList)
    {
        var hit = QueueList.InputHitTest(relativeToList) as DependencyObject;
        while (hit is not null)
        {
            if (hit is FrameworkElement fe && fe.DataContext is QueueItemViewModel vm)
                return vm;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    // ── Priority context menu ──────────────────────────────────────────────

    private void PriorityBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement btn || btn.Tag is not QueueItemViewModel item) return;

        var menu = new ContextMenu { Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(42, 42, 42)) };
        menu.Items.Add(MakeMenuItem("High priority",   item, DownloadPriority.High));
        menu.Items.Add(MakeMenuItem("Normal priority", item, DownloadPriority.Normal));
        menu.Items.Add(MakeMenuItem("Low priority",    item, DownloadPriority.Low));
        menu.PlacementTarget = btn;
        menu.Placement       = PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private MenuItem MakeMenuItem(string header, QueueItemViewModel item, DownloadPriority p)
    {
        var mi = new MenuItem { Header = header, Foreground = System.Windows.Media.Brushes.White };
        mi.Click += (_, _) =>
        {
            if (p == DownloadPriority.High)   _vm.SetHighCommand.Execute(item);
            else if (p == DownloadPriority.Low) _vm.SetLowCommand.Execute(item);
            else                              _vm.SetNormalCommand.Execute(item);
        };
        return mi;
    }
}
