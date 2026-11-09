using System;
using System.Windows;
using System.Windows.Controls;
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

        // Forward routed events from individual GameCardView instances.
        private void Card_PlayGame(object sender, GameRoutedEventArgs e)   => OnPlayGameRequested?.Invoke(e.Game);
        private void Card_PlayModded(object sender, GameRoutedEventArgs e) => OnPlayModdedRequested?.Invoke(e.Game);
        private void Card_GameInfo(object sender, GameRoutedEventArgs e)   => OnGameInfoRequested?.Invoke(e.Game);
    }
}
