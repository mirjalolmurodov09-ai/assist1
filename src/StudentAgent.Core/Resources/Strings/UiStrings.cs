namespace ClassroomControl.StudentAgent.Resources.Strings;

/// <summary>User-facing texts produced by logic (ViewModels / services). Static XAML labels live in the WPF Strings.xaml.</summary>
public static class UiStrings
{
    public const string IndicatorConnected = "🟢 Connected";
    public const string IndicatorConnecting = "🟡 Connecting...";
    public const string IndicatorOffline = "🔴 Offline";

    public const string WaitingForApproval = "Waiting for Teacher approval";
    public const string Registered = "Registered";
    public const string Rejected = "Rejected by Teacher";
    public const string NotRegisteredStudent = "Ro‘yxatdan o‘tmagan";
    public const string NotConnected = "—";

    public const string InvalidClassroomCode = "Classroom code noto‘g‘ri.";
    public const string ClassroomCodeMissing = "Classroom code kiritilmagan. Ulanish uchun kodni kiriting.";
    public const string TeacherNotFound = "Teacher topilmadi. Teacher yoqilganiga va bir tarmoqda ekanligiga ishonch hosil qiling.";
    public const string Discovering = "Teacher qidirilmoqda...";
    public const string Connecting = "Teacher bilan ulanilmoqda...";
    public const string Authenticating = "Autentifikatsiya...";
    public const string Connected = "Teacher bilan ulangan.";
    public const string ConnectionLost = "Aloqa uzildi.";
    public const string RejectedByTeacher = "Teacher bu kompyuterni rad etdi. Qayta ulanish uchun Connect tugmasini bosing.";
    public const string Stopped = "Agent to‘xtatildi.";
    public const string NetworkChanged = "Tarmoq o‘zgardi. Teacher qayta qidirilmoqda...";
    public const string UnexpectedError = "Kutilmagan xatolik yuz berdi. Agent qayta ishga tushirilmoqda.";

    public const string ActivityLocked = "🔒 Ekran o‘qituvchi tomonidan bloklangan.";
    public const string ActivityWatched = "👁 O‘qituvchi ekraningizni kuzatmoqda.";
    public const string ActivityRemote = "🖱 O‘qituvchi kompyuteringizni boshqarmoqda.";
    public const string ActivityTeacherScreen = "📺 O‘qituvchi ekrani ko‘rsatilmoqda.";

    public static string ActivityBlocked(IEnumerable<string> names) => $"⛔ O‘qituvchi bu dasturlarni taqiqladi: {string.Join(", ", names)}.";

    public static string ReconnectingIn(TimeSpan delay) => $"Offline. {delay.TotalSeconds:0} soniyadan keyin qayta uriniladi...";
}
