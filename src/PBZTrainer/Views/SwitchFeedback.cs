using System.Windows;

namespace PBZTrainer.Views
{
    public static class SwitchFeedback
    {
        public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.RegisterAttached(
            "IsLoading", typeof(bool), typeof(SwitchFeedback), new PropertyMetadata(false));

        public static bool GetIsLoading(DependencyObject element) => (bool)element.GetValue(IsLoadingProperty);

        public static void SetIsLoading(DependencyObject element, bool value) => element.SetValue(IsLoadingProperty, value);
    }
}
