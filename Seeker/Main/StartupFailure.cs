using Android.App;
using Android.Content;
using System;
using System.Threading;

namespace Seeker
{
    /// <summary>Holds the first managed startup failure without reading or rewriting saved app data.</summary>
    internal sealed class StartupFailure
    {
        private static StartupFailure current;
        public static StartupFailure Current => current;
        public string Stage { get; }
        public string Summary { get; }
        public string Details { get; }

        private StartupFailure(string stage, Exception exception)
        {
            Stage = stage;
            var cause = exception.GetBaseException();
            Summary = cause.GetType().Name + ": " + cause.Message;
            // Reporting must not make framework/resource calls that could fail again while
            // application initialization is already incomplete.
            Details = "Noiser startup error\nStage: " + stage + "\n\n" + exception;
        }

        public static void Record(string stage, Exception exception)
        {
            Interlocked.CompareExchange(ref current, new StartupFailure(stage, exception), null);
        }

        public static void Show(Activity activity)
        {
            activity.StartActivity(new Intent(activity, typeof(StartupErrorActivity))
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask));
            activity.Finish();
        }
    }
}
