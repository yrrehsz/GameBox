using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class GameCardView : UserControl
    {
        // Routed events bubble up to LibraryGridView, keeping XAML wiring simple.
        public static readonly RoutedEvent PlayGameEvent =
            EventManager.RegisterRoutedEvent(nameof(PlayGame), RoutingStrategy.Bubble,
                typeof(GameEventHandler), typeof(GameCardView));

        public static readonly RoutedEvent PlayModdedEvent =
            EventManager.RegisterRoutedEvent(nameof(PlayModded), RoutingStrategy.Bubble,
                typeof(GameEventHandler), typeof(GameCardView));

        public static readonly RoutedEvent GameInfoEvent =
            EventManager.RegisterRoutedEvent(nameof(GameInfo), RoutingStrategy.Bubble,
                typeof(GameEventHandler), typeof(GameCardView));

        public event GameEventHandler PlayGame   { add => AddHandler(PlayGameEvent,   value); remove => RemoveHandler(PlayGameEvent,   value); }
        public event GameEventHandler PlayModded { add => AddHandler(PlayModdedEvent, value); remove => RemoveHandler(PlayModdedEvent, value); }
        public event GameEventHandler GameInfo   { add => AddHandler(GameInfoEvent,   value); remove => RemoveHandler(GameInfoEvent,   value); }

        public GameCardView()
        {
            InitializeComponent();
        }

        private void BtnPlayGame_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is UnifiedGame game)
                RaiseEvent(new GameRoutedEventArgs(PlayGameEvent, this, game));
        }

        private void BtnPlayModded_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is UnifiedGame game)
                RaiseEvent(new GameRoutedEventArgs(PlayModdedEvent, this, game));
        }

        private void BtnGameInfo_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is UnifiedGame game)
                RaiseEvent(new GameRoutedEventArgs(GameInfoEvent, this, game));
        }

        /// <summary>
        /// Tracks mouse position over the card and shifts the glossy sheen rectangle
        /// so it follows the cursor, creating a dynamic light-reflection effect.
        /// </summary>
        private void CardBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Border cardBorder) return;

            Point pos = e.GetPosition(cardBorder);
            double normX = pos.X / cardBorder.ActualWidth;   // 0.0 (left) → 1.0 (right)
            double normY = pos.Y / cardBorder.ActualHeight;  // 0.0 (top)  → 1.0 (bottom)

            // Find the GlossyOverlay rectangle inside this card and shift it
            if (FindVisualChild<Rectangle>(cardBorder, "GlossyOverlay") is Rectangle gloss &&
                gloss.RenderTransform is TransformGroup tg &&
                tg.Children.Count > 1 &&
                tg.Children[1] is TranslateTransform translate)
            {
                translate.X = (normX - 0.5) * 60;
                translate.Y = normY * 190;
            }
        }

        // No-op: the XAML storyboard handles fading out the gloss on MouseLeave.
        private void CardBorder_MouseLeave(object sender, MouseEventArgs e) { }

        /// <summary>
        /// Walks the visual tree to find a named child of type <typeparamref name="T"/>.
        /// Used to locate the GlossyOverlay rectangle inside the card border.
        /// </summary>
        private static T? FindVisualChild<T>(DependencyObject parent, string childName)
            where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed && typed.Name == childName)
                    return typed;

                var result = FindVisualChild<T>(child, childName);
                if (result != null) return result;
            }
            return null;
        }
    }

    // ---------------------------------------------------------------------------
    // Routed event infrastructure for passing a UnifiedGame through the event args
    // ---------------------------------------------------------------------------

    public delegate void GameEventHandler(object sender, GameRoutedEventArgs e);

    public class GameRoutedEventArgs : RoutedEventArgs
    {
        public UnifiedGame Game { get; }

        public GameRoutedEventArgs(RoutedEvent routedEvent, object source, UnifiedGame game)
            : base(routedEvent, source)
        {
            Game = game;
        }
    }
}
