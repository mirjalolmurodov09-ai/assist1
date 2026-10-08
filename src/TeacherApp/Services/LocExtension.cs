using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace ClassroomControl.TeacherApp
{
    /// <summary>XAML: <c>Text="{l:Loc Main.Title}"</c>. Re-evaluates when the language changes.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension()
        {
            Key = string.Empty;
        }

        public LocExtension(string key)
        {
            Key = key;
        }

        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding
            {
                Source = Loc.Instance,
                Path = new PropertyPath("[(0)]", Key),
                Mode = BindingMode.OneWay,
            };
            return binding.ProvideValue(serviceProvider);
        }
    }
}
