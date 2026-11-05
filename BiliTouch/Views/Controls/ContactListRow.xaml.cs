using System.Windows;
using System.Windows.Controls;
namespace BiliTouch.Views.Controls;
public partial class ContactListRow : UserControl
{
 public ContactListRow()=>InitializeComponent();
 public static readonly DependencyProperty TitleProperty=DependencyProperty.Register(nameof(Title),typeof(string),typeof(ContactListRow),new PropertyMetadata(""));
 public static readonly DependencyProperty SubtitleProperty=DependencyProperty.Register(nameof(Subtitle),typeof(string),typeof(ContactListRow),new PropertyMetadata(""));
 public static readonly DependencyProperty AvatarProperty=DependencyProperty.Register(nameof(Avatar),typeof(string),typeof(ContactListRow),new PropertyMetadata(""));
 public static readonly DependencyProperty IsSelectedProperty=DependencyProperty.Register(nameof(IsSelected),typeof(bool),typeof(ContactListRow),new PropertyMetadata(false));
 public static readonly DependencyProperty HasUnreadProperty=DependencyProperty.Register(nameof(HasUnread),typeof(bool),typeof(ContactListRow),new PropertyMetadata(false));
 public string Title {get=>(string)GetValue(TitleProperty);set=>SetValue(TitleProperty,value);}
 public string Subtitle {get=>(string)GetValue(SubtitleProperty);set=>SetValue(SubtitleProperty,value);}
 public string Avatar {get=>(string)GetValue(AvatarProperty);set=>SetValue(AvatarProperty,value);}
 public bool IsSelected {get=>(bool)GetValue(IsSelectedProperty);set=>SetValue(IsSelectedProperty,value);}
 public bool HasUnread {get=>(bool)GetValue(HasUnreadProperty);set=>SetValue(HasUnreadProperty,value);}
}
