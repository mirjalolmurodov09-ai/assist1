namespace ClassroomControl.TeacherApp.Localization;

/// <summary>UI texts. Bind with <c>{Binding [Key], Source={x:Static loc:Loc.Instance}}</c> (the indexer raises a change notification when the
/// language changes, so the whole UI switches language live). Add a language by adding one dictionary; nothing is hard-coded in views.</summary>
public sealed class Loc : ObservableObject
{
    public const string Uzbek = "uz", English = "en", Russian = "ru";
    public static readonly IReadOnlyList<string> Languages = [Uzbek, English, Russian];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        [Uzbek] = Strings.Uz,
        [English] = Strings.En,
        [Russian] = Strings.Ru,
    };

    private string _language = Uzbek;

    public static Loc Instance { get; } = new();

    public string Language
    {
        get => _language;
        set
        {
            if (!Tables.ContainsKey(value) || value == _language) return;
            _language = value;
            Raise("Item[]");
            Raise();
        }
    }

    public string this[string key] => Tables[_language].TryGetValue(key, out var text) ? text
        : Tables[English].TryGetValue(key, out var fallback) ? fallback : key;

    public string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, this[key], args);

    public static IReadOnlyDictionary<string, string> Table(string language) => Tables[language];

    public static string DisplayName(string language) => language switch
    {
        Uzbek => "O‘zbekcha",
        English => "English",
        Russian => "Русский",
        _ => language,
    };
}
