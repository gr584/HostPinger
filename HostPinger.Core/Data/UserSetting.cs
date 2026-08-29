namespace HostPinger.Core.Data
{
    /// <summary>
    /// One setting changed at runtime, stored as the key it would have in configuration (e.g.
    /// "Pinger:IntervalSeconds"). The table only ever holds the keys somebody has changed;
    /// everything else keeps falling through to appsettings.json and the environment, so a default
    /// changed in a later release shows through for settings nobody has touched.
    /// </summary>
    public class UserSetting
    {
        public string Key { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }
}
