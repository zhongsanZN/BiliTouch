using System.Globalization;
using System.Windows.Data;
namespace BiliTouch.Helpers;
public sealed class MessageCardWidthConverter : IValueConverter
{
 public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
 {var width=Math.Clamp((value is double d?d:420)-64,160,420);return parameter as string=="height"?width*9/16:width;}
 public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
