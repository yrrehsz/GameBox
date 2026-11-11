using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class GameCardView : UserControl
    {
        private static readonly CubicEase HighlightEaseOut = CreateFrozenEaseOut();

        public static readonly DependencyProperty IsControllerSelectedProperty =
            DependencyProperty.Register(
                nameof(IsControllerSelected),
                typeof(bool),
                typeof(GameCardView),
                new PropertyMetadata(false, OnIsControllerSelectedChanged));

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

        private bool? _lastHighlighted;
        private bool? _lastSelectionRing;

        public bool IsControllerSelected
        {
            get => (bool)GetValue(IsControllerSelectedProperty);
            set => SetValue(IsControllerSelectedProperty, value);
        }

        public GameCardView()
        {
            InitializeComponent();
        }

        public void TriggerPlayGame()
        {
            if (DataContext is UnifiedGame game && game.ActionType == GameActionType.Executable)
            {
                RaiseEvent(new GameRoutedEventArgs(PlayGameEvent, this, game));
            }
        }

        public void TriggerPlayModded()
        {
            if (DataContext is UnifiedGame game && !string.IsNullOrEmpty(game.ModLaunchCommand))
            {
                RaiseEvent(new GameRoutedEventArgs(PlayModdedEvent, this, game));
            }
        }

        public void TriggerGameInfo() => RaiseGameAction(GameInfoEvent);

        private void RaiseGameAction(RoutedEvent routedEvent)
        {
            if (DataContext is UnifiedGame game)
            {
                RaiseEvent(new GameRoutedEventArgs(routedEvent, this, game));
            }
        }

        private void BtnPlayGame_Click(object sender, RoutedEventArgs e)   => RaiseGameAction(PlayGameEvent);
        private void BtnPlayModded_Click(object sender, RoutedEventArgs e) => RaiseGameAction(PlayModdedEvent);
        private void BtnGameInfo_Click(object sender, RoutedEventArgs e)   => RaiseGameAction(GameInfoEvent);

        private void CardBorder_MouseEnter(object sender, MouseEventArgs e) => UpdateHighlightVisualState();
        private void CardBorder_MouseLeave(object sender, MouseEventArgs e) => UpdateHighlightVisualState();

        /// <summary>
        /// Tracks mouse position over the card and shifts the glossy sheen rectangle
        /// so it follows the cursor, creating a dynamic light-reflection effect.
        /// </summary>
        private void CardBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Border cardBorder || cardBorder.ActualWidth <= 0 || cardBorder.ActualHeight <= 0)
                return;

            Point pos = e.GetPosition(cardBorder);
            double normX = pos.X / cardBorder.ActualWidth;
            double normY = pos.Y / cardBorder.ActualHeight;

            GlossTranslate.X = (normX - 0.5) * 60;
            GlossTranslate.Y = normY * 190;
        }

        private static void OnIsControllerSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GameCardView card)
            {
                card.UpdateHighlightVisualState();
            }
        }

        private void UpdateHighlightVisualState()
        {
            bool isMouseOver = CardBorder.IsMouseOver;
            bool isSelected = IsControllerSelected;
            bool isHighlighted = isMouseOver || isSelected;

            if (_lastHighlighted != isHighlighted)
            {
                _lastHighlighted = isHighlighted;

                int transformMs = isHighlighted ? 180 : 200;
                double targetScale = isHighlighted ? 1.05 : 1.0;

                AnimateDouble(CardScale, ScaleTransform.ScaleXProperty, targetScale, transformMs, useEaseOut: true);
                AnimateDouble(CardScale, ScaleTransform.ScaleYProperty, targetScale, transformMs, useEaseOut: true);
                AnimateDouble(CardTranslate, TranslateTransform.YProperty, isHighlighted ? -4.0 : 0.0, transformMs, useEaseOut: true);
                AnimateDouble(GlossyOverlay, OpacityProperty, isHighlighted ? 1.0 : 0.0, isHighlighted ? 150 : 250);
                AnimateDouble(ActionOverlayBar, OpacityProperty, isHighlighted ? 1.0 : 0.0, isHighlighted ? 160 : 180);

                ActionOverlayBar.IsHitTestVisible = isHighlighted;

                if (isSelected && !isMouseOver)
                {
                    GlossTranslate.X = 0;
                    GlossTranslate.Y = 80;
                }
            }

            if (_lastSelectionRing != isSelected)
            {
                _lastSelectionRing = isSelected;
                AnimateDouble(ControllerSelectionBorder, OpacityProperty, isSelected ? 1.0 : 0.0, isSelected ? 150 : 180);
            }
        }

        private static void AnimateDouble(
            IAnimatable target,
            DependencyProperty property,
            double toValue,
            int durationMs,
            bool useEaseOut = false)
        {
            var animation = new DoubleAnimation
            {
                To = toValue,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = useEaseOut ? HighlightEaseOut : null
            };
            target.BeginAnimation(property, animation);
        }

        private static CubicEase CreateFrozenEaseOut()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
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
