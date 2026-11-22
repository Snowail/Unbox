using System.Windows;

namespace UnzipProxy
{
    public partial class ExtractionPrompt : Window
    {
        internal Func<bool>? ValidateSubmission { get; set; }

        public ExtractionPrompt()
        {
            InitializeComponent();
        }

        private void OnSubmitClick(object sender, RoutedEventArgs e)
        {
            if (ValidateSubmission?.Invoke() != false)
                DialogResult = true;
        }
    }
}
