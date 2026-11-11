using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class LibraryGridView : UserControl
    {
        private const double RowToleranceY = 40.0;

        public static readonly DependencyProperty IsControllerConnectedProperty =
            DependencyProperty.Register(
                nameof(IsControllerConnected),
                typeof(bool),
                typeof(LibraryGridView),
                new PropertyMetadata(false));

        public event Action<UnifiedGame>? OnPlayGameRequested;
        public event Action<UnifiedGame>? OnPlayModdedRequested;
        public event Action<UnifiedGame>? OnGameInfoRequested;

        private int _selectedIndex = -1;
        private string? _pendingRestoreGameId;
        private Point _lastMousePosition = new(-1, -1);

        public bool IsControllerConnected
        {
            get => (bool)GetValue(IsControllerConnectedProperty);
            set => SetValue(IsControllerConnectedProperty, value);
        }

        public LibraryGridView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Replaces the displayed game list while preserving controller selection if the previously
        /// highlighted game remains visible after filtering or syncing.
        /// </summary>
        public void UpdateGames(List<UnifiedGame> games)
        {
            string? selectedGameId = (_selectedIndex >= 0 && _selectedIndex < GamesGridControl.Items.Count)
                ? (GamesGridControl.Items[_selectedIndex] as UnifiedGame)?.NormalizedId
                : null;

            SetCardSelected(_selectedIndex, false);
            _selectedIndex = -1;
            _pendingRestoreGameId = selectedGameId;

            GamesGridControl.ItemsSource = null;
            GamesGridControl.ItemsSource = games;

            if (selectedGameId == null) return;

            int restoredIndex = games.FindIndex(g => g.NormalizedId == selectedGameId);
            if (restoredIndex < 0)
            {
                _pendingRestoreGameId = null;
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_pendingRestoreGameId == selectedGameId)
                {
                    _pendingRestoreGameId = null;
                    SelectIndex(restoredIndex);
                }
            });
        }

        /// <summary>
        /// Highlights the first game card if none is currently selected.
        /// </summary>
        public void SelectFirstItem()
        {
            if (GamesGridControl.Items.Count == 0) return;

            if (_selectedIndex < 0 || _selectedIndex >= GamesGridControl.Items.Count)
            {
                SelectIndex(0);
            }
        }

        /// <summary>
        /// Clears any active controller selection highlight.
        /// </summary>
        public void ClearSelection()
        {
            _pendingRestoreGameId = null;
            if (_selectedIndex >= 0)
            {
                SetCardSelected(_selectedIndex, false);
                _selectedIndex = -1;
            }
        }

        /// <summary>
        /// Moves controller selection in 2D across the wrapped game grid.
        /// </summary>
        public void Navigate(ControllerButton direction)
        {
            int count = GamesGridControl.Items.Count;
            if (count == 0) return;

            if (_selectedIndex < 0 || _selectedIndex >= count)
            {
                SelectIndex(0);
                return;
            }

            int nextIndex = direction switch
            {
                ControllerButton.DPadLeft  => _selectedIndex > 0 ? _selectedIndex - 1 : _selectedIndex,
                ControllerButton.DPadRight => _selectedIndex + 1 < count ? _selectedIndex + 1 : _selectedIndex,
                ControllerButton.DPadUp    => FindVerticalTargetIndex(_selectedIndex, movingDown: false),
                ControllerButton.DPadDown  => FindVerticalTargetIndex(_selectedIndex, movingDown: true),
                _ => _selectedIndex
            };

            if (nextIndex >= 0 && nextIndex != _selectedIndex)
            {
                SelectIndex(nextIndex);
            }
        }

        /// <summary>
        /// Invokes the primary (A), secondary (X), or info (Y) action on the selected game card.
        /// </summary>
        public void ExecuteAction(ControllerButton action)
        {
            GameCardView? card = GetCardViewAt(_selectedIndex);
            if (card == null) return;

            switch (action)
            {
                case ControllerButton.A:
                    card.TriggerPlayGame();
                    break;
                case ControllerButton.X:
                    card.TriggerPlayModded();
                    break;
                case ControllerButton.Y:
                    card.TriggerGameInfo();
                    break;
            }
        }

        private void SelectIndex(int index)
        {
            int count = GamesGridControl.Items.Count;
            if (index < 0 || index >= count) return;

            _pendingRestoreGameId = null;
            SetCardSelected(_selectedIndex, false);
            _selectedIndex = index;

            if (!TryApplySelectionAndScroll(index))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (_selectedIndex == index)
                    {
                        TryApplySelectionAndScroll(index);
                    }
                });
            }
        }

        private bool TryApplySelectionAndScroll(int index)
        {
            if (GamesGridControl.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                return false;

            if (!container.IsLoaded)
            {
                GamesGridControl.UpdateLayout();
            }

            GameCardView? card = FindVisualDescendant<GameCardView>(container);
            if (card == null) return false;

            card.IsControllerSelected = true;

            if (container.ActualHeight > 0 && container.ActualWidth > 0)
            {
                container.BringIntoView(new Rect(0, -10, container.ActualWidth, container.ActualHeight + 20));
            }
            else
            {
                container.BringIntoView();
            }

            return true;
        }

        private int FindVerticalTargetIndex(int currentIndex, bool movingDown)
        {
            WrapPanel? panel = FindVisualDescendant<WrapPanel>(GamesGridControl);
            if (panel == null) return -1;

            if (!TryGetItemCenter(currentIndex, panel, out Point currentCenter))
                return -1;

            int count = GamesGridControl.Items.Count;
            var candidates = new List<(int Index, Point Center)>();

            for (int i = 0; i < count; i++)
            {
                if (!TryGetItemCenter(i, panel, out Point center)) continue;

                bool isInTargetDirection = movingDown
                    ? center.Y > currentCenter.Y + RowToleranceY
                    : center.Y < currentCenter.Y - RowToleranceY;

                if (isInTargetDirection)
                {
                    candidates.Add((i, center));
                }
            }

            if (candidates.Count == 0) return -1;

            double targetRowY = movingDown
                ? candidates.Min(c => c.Center.Y)
                : candidates.Max(c => c.Center.Y);

            return candidates
                .Where(c => Math.Abs(c.Center.Y - targetRowY) <= RowToleranceY)
                .MinBy(c => Math.Abs(c.Center.X - currentCenter.X))
                .Index;
        }

        private bool TryGetItemCenter(int index, UIElement relativeTo, out Point center)
        {
            center = default;
            if (GamesGridControl.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                return false;
            }

            center = container.TranslatePoint(new Point(container.ActualWidth / 2, container.ActualHeight / 2), relativeTo);
            return true;
        }

        private void SetCardSelected(int index, bool selected)
        {
            GameCardView? card = GetCardViewAt(index);
            if (card != null)
            {
                card.IsControllerSelected = selected;
            }
        }

        private GameCardView? GetCardViewAt(int index)
        {
            if (index < 0 || index >= GamesGridControl.Items.Count) return null;

            if (GamesGridControl.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container)
                return null;

            return FindVisualDescendant<GameCardView>(container);
        }

        private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) return typed;

                T? result = FindVisualDescendant<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void Card_MouseMove(object sender, MouseEventArgs e)
        {
            Point pos = e.GetPosition(this);
            bool mouseActuallyMoved = _lastMousePosition.X >= 0 &&
                                      (Math.Abs(pos.X - _lastMousePosition.X) > 2 ||
                                       Math.Abs(pos.Y - _lastMousePosition.Y) > 2);

            _lastMousePosition = pos;

            if (mouseActuallyMoved && _selectedIndex >= 0)
            {
                ClearSelection();
            }
        }

        // Forward routed events from individual GameCardView instances.
        private void Card_PlayGame(object sender, GameRoutedEventArgs e)   => OnPlayGameRequested?.Invoke(e.Game);
        private void Card_PlayModded(object sender, GameRoutedEventArgs e) => OnPlayModdedRequested?.Invoke(e.Game);
        private void Card_GameInfo(object sender, GameRoutedEventArgs e)   => OnGameInfoRequested?.Invoke(e.Game);
    }
}
