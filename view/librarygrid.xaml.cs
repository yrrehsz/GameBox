using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class LibraryGridView : UserControl
    {
        public event Action<UnifiedGame>? OnPlayGameRequested;
        public event Action<UnifiedGame>? OnPlayModdedRequested;
        public event Action<UnifiedGame>? OnGameInfoRequested;

        public LibraryGridView()
        {
            InitializeComponent();
        }

        private void BtnPlayGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is UnifiedGame game)
            {
                OnPlayGameRequested?.Invoke(game);
            }
        }

        private void BtnPlayModded_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is UnifiedGame game)
            {
                OnPlayModdedRequested?.Invoke(game); 
            }
        }

        // Muove il riflesso lucido (Glossy Sheen) seguendo il trascinamento del cursore sulla copertina!
        private void CardBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Border cardBorder) return;

            Point pos = e.GetPosition(cardBorder);
            double normX = pos.X / cardBorder.ActualWidth;  // 0.0 (sinistra) -> 1.0 (destra)
            double normY = pos.Y / cardBorder.ActualHeight; // 0.0 (alto)     -> 1.0 (basso)

            // Cerca il rettangolo Glossy dentro la card corrente e sposta il riflesso
            if (FindVisualChild<Rectangle>(cardBorder, "GlossyOverlay") is Rectangle gloss &&
                gloss.RenderTransform is TransformGroup tg &&
                tg.Children.Count > 1 &&
                tg.Children[1] is TranslateTransform translate)
            {
                // Fa scorrere la banda di luce in base a dove si trova il cursore
                translate.X = (normX - 0.5) * 60;
                translate.Y = normY * 190;
            }
        }

        private void CardBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            // Quando il mouse esce, l'animazione XAML sfuma via l'opacità del riflesso
        }

        // Helper veloce per trovare il controllo GlossyOverlay dentro il DataTemplate
        private static T? FindVisualChild<T>(DependencyObject parent, string childName) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild && typedChild.Name == childName)
                    return typedChild;

                var result = FindVisualChild<T>(child, childName);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void BtnGameInfo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is UnifiedGame game)
            {
                OnGameInfoRequested?.Invoke(game);
            }
        }
    }
}