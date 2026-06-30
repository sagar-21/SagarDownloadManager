// Disambiguate WPF vs WinForms/GDI+ types that clash when UseWindowsForms=true.
// WPF types win everywhere; MainWindow.xaml.cs aliases WinForms explicitly as needed.
global using Application  = System.Windows.Application;
global using UserControl  = System.Windows.Controls.UserControl;
global using Brush        = System.Windows.Media.Brush;
global using Brushes      = System.Windows.Media.Brushes;
global using Color        = System.Windows.Media.Color;
global using Point        = System.Windows.Point;
global using Size         = System.Windows.Size;
global using Rect         = System.Windows.Rect;
global using KeyEventArgs    = System.Windows.Input.KeyEventArgs;
global using MessageBox      = System.Windows.MessageBox;
global using Button          = System.Windows.Controls.Button;
global using ColorConverter  = System.Windows.Media.ColorConverter;
global using Pen             = System.Windows.Media.Pen;
global using System.Net.Http;
