using Android.Content;
using Android.Views;
using Android.Widget;
using Common;
using Common.Browse;
using Google.Android.Material.Dialog;
using Seeker.Helpers;
using System;
using System.IO;
using System.Linq;

namespace Seeker.Browse
{
    internal static class FolderBookmarks
    {
        private static FolderBookmarkStore store;
        private static FolderBookmarkStore Store(Context context) => store ??= new FolderBookmarkStore(
            Path.Combine(context.ApplicationContext.FilesDir.AbsolutePath, "folder-bookmarks-v1.json"));

        internal static bool Contains(Context context, string username, string path) => Store(context).Contains(username, path);
        private static void Refresh() => SeekerState.MainActivityRef?.InvalidateOptionsMenu();
        private static void Toast(Context context, int message) => SeekerApplication.Toaster.ShowToast(context.GetString(message), ToastLength.Short);
        private static bool Try(Context context, Action action)
        {
            try { action(); return true; }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                Logger.Debug("Folder bookmark operation failed: " + error.GetType().Name);
                Toast(context, Resource.String.bookmark_save_failed); return false;
            }
        }
        internal static void Add(Context context, string username, string path)
        {
            Try(context, () =>
            {
                bool added = Store(context).Add(username, path);
                Toast(context, added ? Resource.String.bookmark_added : Resource.String.bookmark_exists);
                Refresh();
            });
        }
        internal static void Toggle(Context context, string username, string path)
        {
            Try(context, () =>
            {
                var entry = Store(context).Find(username, path);
                if (entry == null) { Add(context, username, path); return; }
                Store(context).Remove(entry.Id);
                Toast(context, Resource.String.bookmark_removed); Refresh();
            });
        }
        internal static void Show(Context context, bool manage = false)
        {
            var entries = Store(context).All().ToArray();
            if (entries.Length == 0)
            {
                new MaterialAlertDialogBuilder(context).SetTitle(Resource.String.bookmark_list)
                    .SetMessage(Resource.String.bookmark_empty).SetPositiveButton(Resource.String.close, (_, __) => { }).Show();
                return;
            }
            var adapter = new BookmarkAdapter(context, entries);
            var dialog = new MaterialAlertDialogBuilder(context)
                .SetTitle(manage ? Resource.String.bookmark_manage : Resource.String.bookmark_list)
                .SetAdapter(adapter, (_, args) =>
                {
                    if (args.Which < 0 || args.Which >= entries.Length) return;
                    if (manage) Edit(context, entries[args.Which]);
                    else Open(context, entries[args.Which]);
                })
                .SetNegativeButton(Resource.String.close, (_, __) => { });
            if (!manage) dialog.SetNeutralButton(Resource.String.bookmark_manage, (_, __) => Show(context, true));
            var window = dialog.Show();
            window.ListView.ItemLongClick += (_, args) =>
            {
                if (args.Position < 0 || args.Position >= entries.Length) return;
                args.Handled = true;
                window.Dismiss(); Edit(context, entries[args.Position]);
            };
        }
        private static void Open(Context context, FolderBookmark entry)
        {
            if (!PreferencesState.CurrentlyLoggedIn) { Toast(context, Resource.String.must_be_logged_to_browse); return; }
            if (context is MainActivity main)
                main.FindViewById<AndroidX.ViewPager2.Widget.ViewPager2>(Resource.Id.pager)?.SetCurrentItem(3, true);
            else
            {
                var intent = new Intent(context, typeof(MainActivity));
                intent.PutExtra(MainActivity.GoToBrowseExtra, true);
                intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
                context.StartActivity(intent);
            }
            BrowseService.RequestFilesApi(entry.Username, entry.FolderPath.Length == 0 ? null : entry.FolderPath);
        }
        private static void Edit(Context context, FolderBookmark entry)
        {
            string[] options = { context.GetString(Resource.String.bookmark_open), context.GetString(Resource.String.bookmark_rename), context.GetString(Resource.String.bookmark_remove) };
            new MaterialAlertDialogBuilder(context).SetTitle(entry.Label).SetItems(options, (_, args) =>
            {
                if (args.Which == 0) Open(context, entry);
                else if (args.Which == 1) Rename(context, entry);
                else if (args.Which == 2 && Try(context, () => Store(context).Remove(entry.Id)))
                { Refresh(); Show(context); }
            }).SetNegativeButton(Resource.String.cancel, (_, __) => { }).Show();
        }
        private static void Rename(Context context, FolderBookmark entry)
        {
            var input = new EditText(context) { Text = entry.Label, ContentDescription = context.GetString(Resource.String.bookmark_name) };
            input.SetSingleLine(true);
            input.SetSelectAllOnFocus(true);
            new MaterialAlertDialogBuilder(context).SetTitle(Resource.String.bookmark_rename).SetView(input)
                .SetPositiveButton(Resource.String.Save, (_, __) =>
                {
                    string label = input.Text?.Trim() ?? "";
                    if (label.Length == 0 || label.Length > 200)
                    { Toast(context, Resource.String.bookmark_bad_name); Rename(context, entry); return; }
                    if (Try(context, () => Store(context).Rename(entry.Id, label))) { Refresh(); Show(context); }
                }).SetNegativeButton(Resource.String.cancel, (_, __) => { }).Show();
        }
        private sealed class BookmarkAdapter : BaseAdapter<FolderBookmark>
        {
            private readonly Context context;
            private readonly FolderBookmark[] entries;
            public BookmarkAdapter(Context context, FolderBookmark[] entries) { this.context = context; this.entries = entries; }
            public override int Count => entries.Length;
            public override FolderBookmark this[int position] => entries[position];
            public override long GetItemId(int position) => position;
            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                var row = convertView ?? LayoutInflater.From(context).Inflate(Resource.Layout.row_about_link, parent, false);
                row.Clickable = row.Focusable = false;
                row.FindViewById<ImageView>(Resource.Id.rowIcon).SetImageResource(Resource.Drawable.folder_outline);
                row.FindViewById(Resource.Id.rowExternal).Visibility = ViewStates.Gone;
                row.FindViewById<TextView>(Resource.Id.rowTitle).Text = entries[position].Label;
                var subtitle = row.FindViewById<TextView>(Resource.Id.rowSubtitle);
                subtitle.Text = entries[position].Username + " · " + (entries[position].FolderPath.Length == 0
                    ? context.GetString(Resource.String.bookmark_root) : entries[position].FolderPath);
                subtitle.Alpha = 1f;
                subtitle.SetMaxLines(2);
                subtitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                subtitle.Visibility = ViewStates.Visible;
                return row;
            }
        }
    }
}
