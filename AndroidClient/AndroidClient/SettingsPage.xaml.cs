using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace AndroidClient
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class SettingsPage : ContentPage
    {
        public SettingsPage()
        {
            InitializeComponent();

            this.BindingContext = Settings.Instance;
        }
    }
}