using System.Windows;

namespace UnzipProxy
{
    public partial class ExtractionPrompt : Window
    {
        public ExtractionPrompt()
        {
            InitializeComponent();
        }

        private void OnSubmitClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
