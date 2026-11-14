package lan.codex.fivepoint;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.os.SystemClock;
import android.view.Display;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityEvent;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

public final class ClickService extends AccessibilityService {
    static ClickService current;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Runner runner = new Runner(new Runner.Scheduler() {
        public long now() { return SystemClock.uptimeMillis(); }
        public void post(Runnable task, long delay) { main.postDelayed(task, delay); }
        public void remove(Runnable task) { main.removeCallbacks(task); }
    }, this::tap, this::updateSoon);
    private WindowManager windows;
    private Store store;
    private LinearLayout bar;
    private FrameLayout picker;
    private WindowManager.LayoutParams barParams;
    private TextView summary;
    private Button pause, start, add;
    private Screen overlayScreen;
    private boolean visible, screenReceiverRegistered;
    private boolean refreshQueued;
    private final BroadcastReceiver screenReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            runner.stop("屏幕关闭，已停止");
            closePicker();
        }
    };

    @Override protected void onServiceConnected() {
        windows = (WindowManager) getSystemService(WINDOW_SERVICE);
        store = new Store(this);
        current = this;
        if (!screenReceiverRegistered) {
            IntentFilter filter = new IntentFilter(Intent.ACTION_SCREEN_OFF);
            if (Build.VERSION.SDK_INT >= 33) registerReceiver(screenReceiver, filter, RECEIVER_NOT_EXPORTED);
            else registerReceiver(screenReceiver, filter);
            screenReceiverRegistered = true;
        }
    }

    @Override public void onAccessibilityEvent(AccessibilityEvent event) { }

    @Override public void onInterrupt() {
        runner.stop("无障碍服务中断");
        closePicker();
    }

    @Override public boolean onUnbind(Intent intent) {
        cleanup();
        return super.onUnbind(intent);
    }

    @Override public void onDestroy() {
        cleanup();
        super.onDestroy();
    }

    private void cleanup() {
        if (current == this) current = null;
        runner.stop("无障碍服务已关闭");
        hideToolbar();
        if (screenReceiverRegistered) {
            unregisterReceiver(screenReceiver);
            screenReceiverRegistered = false;
        }
    }

    @Override public void onConfigurationChanged(Configuration config) {
        super.onConfigurationChanged(config);
        if (visible && !screen().sameAs(overlayScreen)) {
            runner.stop("屏幕方向或分辨率已变更");
            hideToolbar();
        }
    }

    Screen screen() {
        Display display = windows.getDefaultDisplay();
        android.graphics.Point size = new android.graphics.Point();
        display.getRealSize(size);
        return new Screen(size.x, size.y, display.getRotation());
    }

    boolean busy() { return runner.state() != Runner.State.IDLE; }

    void showToolbar() {
        if (visible) {
            Toast.makeText(this, "悬浮栏已显示，切到目标应用后可取点或开始。", Toast.LENGTH_SHORT).show();
            return;
        }
        overlayScreen = screen();
        bar = new LinearLayout(this);
        bar.setOrientation(LinearLayout.VERTICAL);
        bar.setPadding(dp(8), dp(5), dp(8), dp(6));
        bar.setBackground(shape(0xf5213455, 15));
        bar.setElevation(dp(5));

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        summary = new TextView(this);
        summary.setTextColor(Color.WHITE);
        summary.setTextSize(12);
        summary.setPadding(dp(5), dp(7), dp(5), dp(7));
        header.addView(summary, new LinearLayout.LayoutParams(0, -2, 1));
        Button hide = control("×", 0xff394e70);
        hide.setContentDescription("停止并隐藏悬浮栏");
        hide.setOnClickListener(view -> {
            runner.stop("已停止");
            hideToolbar();
        });
        header.addView(hide, new LinearLayout.LayoutParams(dp(40), dp(40)));
        bar.addView(header);

        LinearLayout actions = new LinearLayout(this);
        add = control("取点", 0xff394e70);
        start = control("开始", 0xff1858cd);
        pause = control("暂停", 0xff394e70);
        Button stop = control("停止", 0xffae3545);
        add.setOnClickListener(view -> pick());
        start.setOnClickListener(view -> startRun());
        pressImmediately(pause, runner::togglePause);
        pressImmediately(stop, () -> runner.stop("已停止"));
        for (Button button : new Button[] { add, start, pause, stop }) {
            LinearLayout.LayoutParams item = new LinearLayout.LayoutParams(dp(55), dp(45));
            item.setMargins(dp(2), 0, dp(2), 0);
            actions.addView(button, item);
        }
        bar.addView(actions);

        barParams = overlayParams(-2, -2);
        barParams.x = dp(12);
        barParams.y = dp(44);
        summary.setOnTouchListener(new View.OnTouchListener() {
            float x, y;
            int left, top;
            @Override public boolean onTouch(View view, MotionEvent event) {
                if (event.getAction() == MotionEvent.ACTION_DOWN) {
                    x = event.getRawX();
                    y = event.getRawY();
                    left = barParams.x;
                    top = barParams.y;
                    return true;
                }
                if (event.getAction() == MotionEvent.ACTION_MOVE && !busy()) {
                    Screen screen = screen();
                    barParams.x = clamp(left + Math.round(event.getRawX() - x), 0, screen.width - bar.getWidth());
                    barParams.y = clamp(top + Math.round(event.getRawY() - y), 0, screen.height - bar.getHeight());
                    windows.updateViewLayout(bar, barParams);
                }
                return true;
            }
        });
        windows.addView(bar, barParams);
        visible = true;
        refresh();
    }

    private void hideToolbar() {
        closePicker();
        if (visible) {
            windows.removeViewImmediate(bar);
            visible = false;
        }
    }

    private void pick() {
        if (busy() || picker != null) return;
        Config config = store.load();
        try { config.freeName(); }
        catch (IllegalStateException e) { toast(e.getMessage()); return; }
        Screen source = screen();
        if (!config.points.isEmpty() && !source.matches(config)) {
            toast("屏幕布局与历史点位不同，请到主界面清空或重新录入坐标。");
            return;
        }
        picker = new FrameLayout(this);
        picker.setBackgroundColor(0x252058cd);
        TextView prompt = new TextView(this);
        prompt.setText("轻触目标位置以记录坐标");
        prompt.setTextColor(Color.WHITE);
        prompt.setTextSize(16);
        prompt.setGravity(Gravity.CENTER);
        prompt.setBackground(shape(0xf5213455, 12));
        FrameLayout.LayoutParams promptParams = new FrameLayout.LayoutParams(-1, dp(50), Gravity.TOP);
        promptParams.setMargins(dp(16), dp(48), dp(16), 0);
        picker.addView(prompt, promptParams);
        Button cancel = control("取消取点", 0xff394e70);
        FrameLayout.LayoutParams cancelParams = new FrameLayout.LayoutParams(dp(100), dp(48),
            Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        cancelParams.bottomMargin = dp(56);
        picker.addView(cancel, cancelParams);
        cancel.setOnClickListener(view -> closePicker());
        picker.setOnTouchListener((view, event) -> {
            if (event.getActionMasked() == MotionEvent.ACTION_UP) {
                int x = (int) event.getRawX();
                int y = (int) event.getRawY();
                closePicker();
                if (source.sameAs(screen()) && x >= 0 && y >= 0 && x < source.width && y < source.height) {
                    java.util.ArrayList<Config.Point> list = new java.util.ArrayList<>(config.points);
                    String name = config.freeName();
                    list.add(new Config.Point(name, x, y, 1, true, false));
                    store.save(config.withPoints(list, source.width, source.height, source.rotation));
                    toast(name + "  (" + x + ", " + y + ")");
                    refresh();
                }
            }
            return true;
        });
        try { windows.addView(picker, overlayParams(-1, -1)); }
        catch (RuntimeException e) {
            picker = null;
            toast("无法显示取点层");
        }
    }

    private void closePicker() {
        if (picker != null) {
            if (picker.isAttachedToWindow()) windows.removeViewImmediate(picker);
            picker = null;
        }
    }

    private Rect barBounds() {
        int[] location = new int[2];
        bar.getLocationOnScreen(location);
        return new Rect(location[0], location[1], location[0] + bar.getWidth(), location[1] + bar.getHeight());
    }

    private void startRun() {
        try {
            Config config = store.load();
            Screen screen = screen();
            if (!screen.matches(config)) throw new IllegalArgumentException("请先录入当前屏幕方向和分辨率下的点位。");
            Rect obstructed = barBounds();
            for (Config.Point point : config.points) {
                if (!point.enabled) continue;
                if (point.x >= screen.width || point.y >= screen.height)
                    throw new IllegalArgumentException(point.name + " 点超出屏幕。");
                if (obstructed.contains(point.x, point.y))
                    throw new IllegalArgumentException("请先拖动悬浮栏，避开 " + point.name + " 点。");
            }
            runner.start(config);
        } catch (IllegalArgumentException e) { toast(e.getMessage()); }
    }

    private boolean tap(Config.Point point, Runner.Result result) {
        PowerManager power = (PowerManager) getSystemService(POWER_SERVICE);
        android.app.KeyguardManager lock = (android.app.KeyguardManager) getSystemService(KEYGUARD_SERVICE);
        if (!power.isInteractive() || lock.isKeyguardLocked() || !visible || !screen().sameAs(overlayScreen)
                || barBounds().contains(point.x, point.y)) return false;
        Path path = new Path();
        path.moveTo(point.x, point.y);
        GestureDescription gesture = new GestureDescription.Builder()
            .addStroke(new GestureDescription.StrokeDescription(path, 0, 16)).build();
        try {
            return dispatchGesture(gesture, new GestureResultCallback() {
                @Override public void onCompleted(GestureDescription description) { result.done(true); }
                @Override public void onCancelled(GestureDescription description) { result.done(false); }
            }, main);
        } catch (IllegalStateException e) {
            return false;
        }
    }

    private void updateSoon() {
        if (refreshQueued) return;
        refreshQueued = true;
        main.postDelayed(() -> {
            refreshQueued = false;
            refresh();
        }, 120);
    }

    private void refresh() {
        if (!visible) return;
        Runner.State state = runner.state();
        Config.Point point = runner.latest();
        String prefix;
        switch (state) {
            case COUNTDOWN: prefix = "准备 " + runner.countdownLeft() + " 秒"; break;
            case RUNNING: prefix = "运行 " + (point == null ? "" : point.name); break;
            case PAUSED: prefix = "已暂停"; break;
            case STOPPING: prefix = "正在停止"; break;
            default: prefix = runner.notice();
        }
        summary.setText(prefix + "  ·  " + runner.clicks() + " 次 / " + runner.rounds() + " 轮");
        if (point != null) {
            summary.setContentDescription(summary.getText() + "，最近点位 " + point.name + "，坐标 " + point.x + ", " + point.y);
        } else {
            summary.setContentDescription(null);
        }
        start.setEnabled(state == Runner.State.IDLE);
        add.setEnabled(state == Runner.State.IDLE);
        pause.setEnabled(state == Runner.State.RUNNING || state == Runner.State.PAUSED);
        pause.setText(state == Runner.State.PAUSED ? "继续" : "暂停");
        if (state == Runner.State.COUNTDOWN) updateSoon();
    }

    private WindowManager.LayoutParams overlayParams(int width, int height) {
        WindowManager.LayoutParams params = new WindowManager.LayoutParams(width, height,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
            PixelFormat.TRANSLUCENT);
        params.gravity = Gravity.TOP | Gravity.LEFT;
        return params;
    }

    private void pressImmediately(Button button, Runnable action) {
        button.setOnClickListener(view -> action.run());
        button.setOnTouchListener((view, event) -> {
            if (event.getActionMasked() == MotionEvent.ACTION_DOWN && button.isEnabled()) {
                button.performClick();
            }
            return true;
        });
    }

    private Button control(String label, int color) {
        Button button = new Button(this);
        button.setText(label);
        button.setAllCaps(false);
        button.setTextColor(Color.WHITE);
        button.setTextSize(13);
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setPadding(0, 0, 0, 0);
        button.setBackgroundTintList(ColorStateList.valueOf(color));
        return button;
    }

    private GradientDrawable shape(int color, int radius) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(color);
        shape.setCornerRadius(dp(radius));
        return shape;
    }

    private int clamp(int value, int min, int max) { return Math.max(min, Math.min(max, value)); }
    private int dp(int value) { return Math.round(getResources().getDisplayMetrics().density * value); }
    private void toast(String text) { Toast.makeText(this, text, Toast.LENGTH_SHORT).show(); }

    static final class Screen {
        final int width, height, rotation;
        Screen(int width, int height, int rotation) {
            this.width = width;
            this.height = height;
            this.rotation = rotation;
        }
        boolean sameAs(Screen screen) {
            return screen != null && width == screen.width && height == screen.height && rotation == screen.rotation;
        }
        boolean matches(Config config) {
            return width == config.width && height == config.height && rotation == config.rotation;
        }
    }
}

