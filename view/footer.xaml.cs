using System;
using System.Windows;
using System.Windows.Controls;

namespace GameStartMenu.Views
{
    public partial class FooterView : UserControl
    {
        private ControllerInfo? _selectedController;

        public event Action<ControllerInfo>? OnIdentifyRequested;
        public event Action<ControllerInfo>? OnTurnOffRequested;

        public FooterView()
        {
            InitializeComponent();
        }

        private void BtnControllerItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is ControllerInfo info)
            {
                _selectedController = info;
                ControllerActionPopup.PlacementTarget = btn;
                ControllerActionPopup.IsOpen = true;
            }
        }

        private void BtnIdentifyController_Click(object sender, RoutedEventArgs e)
        {
            ControllerActionPopup.IsOpen = false;
            if (_selectedController != null)
            {
                OnIdentifyRequested?.Invoke(_selectedController);
            }
        }

        private void BtnTurnOffController_Click(object sender, RoutedEventArgs e)
        {
            ControllerActionPopup.IsOpen = false;
            if (_selectedController != null)
            {
                OnTurnOffRequested?.Invoke(_selectedController);
            }
        }
    }
}