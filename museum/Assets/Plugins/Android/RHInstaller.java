package world.realityhack.museum;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.StatFs;
import android.provider.Settings;

import com.unity3d.player.UnityPlayer;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Standalone install mode (CLAUDE.md phase 5): PackageInstaller sessions with REQUEST_INSTALL_PACKAGES.
 * Android shows one confirmation per app; results come back to Unity as
 * UnitySendMessage(callbackObject, "OnPackageResult", "op|package|status|message").
 * status: PackageInstaller.STATUS_* (0 = success, -1 = waiting for the user, 3 = user cancelled).
 */
public final class RHInstaller {
    private static final String ACTION = "world.realityhack.museum.PACKAGE_STATUS";
    private static BroadcastReceiver receiver;
    private static String callbackObject = "RHAppManager";

    private RHInstaller() { }

    public static void init(final Activity activity, String unityObject) {
        callbackObject = unityObject;
        if (receiver != null) return;
        receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, -999);
                String op = intent.getStringExtra("rh_op");
                String pkg = intent.getStringExtra("rh_package");
                String msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
                if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                    Intent confirm = intent.getParcelableExtra(Intent.EXTRA_INTENT);
                    if (confirm != null) {
                        confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                        context.startActivity(confirm);
                    }
                }
                UnityPlayer.UnitySendMessage(callbackObject, "OnPackageResult",
                        op + "|" + pkg + "|" + status + "|" + (msg == null ? "" : msg.replace('|', '/')));
            }
        };
        IntentFilter filter = new IntentFilter(ACTION);
        if (Build.VERSION.SDK_INT >= 33) {
            activity.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            activity.registerReceiver(receiver, filter);
        }
    }

    public static boolean canInstall(Activity activity) {
        return Build.VERSION.SDK_INT < 26 || activity.getPackageManager().canRequestPackageInstalls();
    }

    /** Opens "Install unknown apps" for the museum; the user flips it once. */
    public static void openInstallPermissionSettings(Activity activity) {
        Intent i = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + activity.getPackageName()));
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        activity.startActivity(i);
    }

    /** Returns "" when the session was committed (the result arrives via OnPackageResult), else an error. */
    public static String install(Activity activity, String apkPath, String packageName) {
        PackageInstaller.Session session = null;
        try {
            PackageInstaller installer = activity.getPackageManager().getPackageInstaller();
            PackageInstaller.SessionParams params =
                    new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
            params.setAppPackageName(packageName);
            File apk = new File(apkPath);
            params.setSize(apk.length());
            int id = installer.createSession(params);
            session = installer.openSession(id);
            InputStream in = new FileInputStream(apk);
            OutputStream out = session.openWrite("base.apk", 0, apk.length());
            try {
                byte[] buf = new byte[1 << 16];
                int n;
                while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                session.fsync(out);
            } finally {
                in.close();
                out.close();
            }
            session.commit(pending(activity, "install", packageName, id).getIntentSender());
            return "";
        } catch (Exception e) {
            if (session != null) session.abandon();
            return e.toString();
        } finally {
            if (session != null) session.close();
        }
    }

    public static String uninstall(Activity activity, String packageName) {
        try {
            activity.getPackageManager().getPackageInstaller()
                    .uninstall(packageName, pending(activity, "uninstall", packageName, packageName.hashCode()).getIntentSender());
            return "";
        } catch (Exception e) {
            return e.toString();
        }
    }

    private static PendingIntent pending(Context context, String op, String packageName, int requestCode) {
        Intent i = new Intent(ACTION).setPackage(context.getPackageName());
        i.putExtra("rh_op", op);
        i.putExtra("rh_package", packageName);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        // PackageInstaller fills in EXTRA_STATUS, so the PendingIntent must be mutable on Android 12+.
        if (Build.VERSION.SDK_INT >= 31) flags |= PendingIntent.FLAG_MUTABLE;
        return PendingIntent.getBroadcast(context, requestCode, i, flags);
    }

    /** lastUpdateTime in ms, or -1 when not installed. */
    public static long installedAt(Context context, String packageName) {
        try {
            PackageInfo info = context.getPackageManager().getPackageInfo(packageName, 0);
            return info.lastUpdateTime;
        } catch (PackageManager.NameNotFoundException e) {
            return -1;
        }
    }

    public static boolean launch(Activity activity, String packageName, String returnTo) {
        Intent i = activity.getPackageManager().getLaunchIntentForPackage(packageName);
        if (i == null) return false;
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        i.putExtra("museumReturnTo", returnTo);
        activity.startActivity(i);
        return true;
    }

    public static long freeBytes(String path) {
        return new StatFs(path).getAvailableBytes();
    }
}
