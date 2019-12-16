using Xamarin.Forms;

namespace AndroidClient
{
    public class Settings
    {
        public static Settings Instance = new Settings();
        #region Settings
        public string PocketAuthCode
        {
            get { return ReadSetting(nameof(PocketAuthCode), ""); }
            set { SetSetting(nameof(PocketAuthCode), value); }
        }

        public bool ShowThumbnails
        {
            get { return ReadSetting<bool?>(nameof(ShowThumbnails), true).Value; }
            set { SetSetting<bool?>(nameof(ShowThumbnails), value); }
        }
        public bool DownloadAllOnGet
        {
            get { return ReadSetting<bool?>(nameof(DownloadAllOnGet), false).Value; }
            set { SetSetting<bool?>(nameof(DownloadAllOnGet), value); }
        }

        public int ChunkSize
        {
            get { return ReadSetting(nameof(ChunkSize), 0); }
            set { SetSetting(nameof(ChunkSize), value); }
        }
        #endregion Settings

        private void SetSetting<T>(string setting, T value)
        {
            if (!Application.Current.Properties.ContainsKey(setting))
                Application.Current.Properties.Add(setting, value);
            else
                Application.Current.Properties[setting] = value;
        }

        private T ReadSetting<T>(string setting, T defaultValue)
        {
            if (!Application.Current.Properties.ContainsKey(setting))
                Application.Current.Properties.Add(setting, defaultValue);

            return (T)Application.Current.Properties[setting];
        }
    }
}
