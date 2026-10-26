using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Widget;
using System;

namespace Seeker
{
    // Use platform widgets so reporting a failure does not depend on the app's theme,
    // fragments, restored preferences, playback service, or logger being initialized.
    [Activity(Name = "com.companyname.andriodapp1.StartupErrorActivity", Exported = false,
        Theme = "@android:style/Theme.Material.Light.NoActionBar", Label = "Noiser startup error")]
    public sealed class StartupErrorActivity : Activity
    {
        public StartupErrorActivity() { }
        public StartupErrorActivity(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            var failure = StartupFailure.Current;
            var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            int padding = (int)(24 * Resources.DisplayMetrics.Density);
            content.SetPadding(padding, padding, padding, padding);
            var title = new TextView(this) { Text = "Noiser couldn't start", TextSize = 24 };
            content.AddView(title);
            content.AddView(new TextView(this)
            {
                Text = "You can type the message below into our conversation, or tap Copy error details.",
                TextSize = 17
            });
            var message = new TextView(this)
            {
                Text = failure == null ? "No startup error was recorded."
                    : failure.Stage + "\n\n" + failure.Summary,
                TextSize = 17
            };
            message.SetTextIsSelectable(true);
            var scroll = new ScrollView(this);
            scroll.AddView(message);
            content.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
            var copy = new Button(this) { Text = "Copy error details" };
            copy.Click += (_, __) =>
            {
                var clipboard = (ClipboardManager)GetSystemService(ClipboardService);
                clipboard.PrimaryClip = ClipData.NewPlainText("Noiser startup error", failure?.Details ?? message.Text);
                copy.Text = "Copied — paste into the conversation";
            };
            content.AddView(copy);
            var close = new Button(this) { Text = "Close" };
            close.Click += (_, __) => FinishAffinity();
            content.AddView(close);
            SetContentView(content);
        }
    }
}
