package lan.codex.fivepoint;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

public final class MainActivity extends Activity {
    private static final int BLUE = 0xff1858cd, INK = 0xff1c314f, GRAY = 0xff526784;
    private static final int IMPORT = 21, EXPORT = 22;
    private Store store;
    private LinearLayout body;
    private TextView serviceStatus;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        store = new Store(this);
        getWindow().setStatusBarColor(0xff142a4d);
        getWindow().setNavigationBarColor(0xff142a4d);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(0xffeef3fa);
        body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(dp(16), dp(14), dp(16), dp(16));
        scroll.addView(body);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> {
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom);
            }
            return insets;
        });
        setContentView(scroll);
    }

    @Override public void onResume() {
        super.onResume();
        render();
    }

    private void render() {
        body.removeAllViews();
        Config config = store.load();
        LinearLayout hero = section(0xff142a4d);
        hero.addView(text("五点连点器", 24, Color.WHITE, true));
        serviceStatus = text(ClickService.current == null ? "无障碍服务未开启" : "无障碍服务已连接",
            14, 0xffc5dbff, false);
        hero.addView(serviceStatus);
        LinearLayout permissions = horizontal(hero);
        action(permissions, "开无障碍", BLUE, () -> startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)));
        action(permissions, "显示悬浮栏", BLUE, () -> {
            ClickService service = ClickService.current;
            if (service == null) {
                toast("请先在无障碍设置启用「五点连点器」。");
                return;
            }
            try { service.showToolbar(); }
            catch (RuntimeException e) { toast("系统未能显示无障碍悬浮栏。"); }
        });

        LinearLayout points = section(Color.WHITE);
        points.addView(text("点位顺序  " + config.points.size() + " / 5", 18, INK, true));
        if (config.points.isEmpty()) {
            points.addView(text("打开悬浮栏，在目标应用取点，或在此输入坐标。", 14, GRAY, false));
        }
        for (int index = 0; index < config.points.size(); index++) {
            final int row = index;
            Config.Point p = config.points.get(index);
            String label = p.name + "   (" + p.x + ", " + p.y + ")   "
                + (p.doubleTap ? "双击" : "单击") + " × " + p.repeat + (p.enabled ? "" : " · 已禁用");
            LinearLayout line = horizontal(points);
            Button edit = makeButton(label, 0xffe9f0fc, INK);
            edit.setOnClickListener(view -> editPoint(row));
            line.addView(edit, new LinearLayout.LayoutParams(0, dp(50), 1));
            action(line, "↑", 0xffe9f0fc, () -> shift(row, -1)).setEnabled(index > 0);
            action(line, "↓", 0xffe9f0fc, () -> shift(row, 1)).setEnabled(index + 1 < config.points.size());
        }
        LinearLayout editActions = horizontal(points);
        action(editActions, "手动添加", BLUE, () -> editPoint(-1)).setEnabled(config.points.size() < Config.MAX_POINTS);
        action(editActions, "清空点位", 0xffe9f0fc, () -> {
            if (!editable()) return;
            new AlertDialog.Builder(this).setMessage("清空全部点位？")
                .setNegativeButton("取消", null)
                .setPositiveButton("清空", (dialog, which) -> {
                    if (editable()) commit(config.withPoints(Collections.emptyList(), 0, 0, 0));
                }).show();
        }).setEnabled(!config.points.isEmpty());

        LinearLayout timing = section(Color.WHITE);
        timing.addView(text("运行参数", 18, INK, true));
        String mode = config.mode == Config.Mode.INFINITE ? "无限循环" :
            config.mode == Config.Mode.ROUNDS ? config.rounds + " 轮" : config.seconds + " 秒";
        timing.addView(text(mode + " · 点间隔 " + config.pointMs + " ms · 轮间隔 " + config.roundMs
            + " ms · 倒计时 " + config.countdown + " 秒", 14, GRAY, false));
        action(horizontal(timing), "修改参数", BLUE, this::editTiming);

        LinearLayout files = section(Color.WHITE);
        files.addView(text("配置文件", 18, INK, true));
        LinearLayout fileActions = horizontal(files);
        action(fileActions, "导入 JSON", 0xffe9f0fc, () -> {
            if (editable()) startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                .setType("application/json"), IMPORT);
        });
        action(fileActions, "导出 JSON", 0xffe9f0fc, () -> startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT)
            .addCategory(Intent.CATEGORY_OPENABLE).setType("application/json")
            .putExtra(Intent.EXTRA_TITLE, "five-point-android.json"), EXPORT));
    }

    private void editPoint(int row) {
        if (!editable()) return;
        Config config = store.load();
        boolean adding = row < 0;
        try {
            Config.Point old = adding ? new Config.Point(config.freeName(), 0, 0, 1, true, false) : config.points.get(row);
            LinearLayout form = form();
            EditText x = number(form, "X 坐标（屏幕像素）", old.x);
            EditText y = number(form, "Y 坐标（屏幕像素）", old.y);
            EditText count = number(form, "执行次数（1–1000）", old.repeat);
            CheckBox enabled = check(form, "启用", old.enabled);
            CheckBox doubleTap = check(form, "双击", old.doubleTap);
            AlertDialog.Builder builder = formDialog("点位 " + old.name, form);
            if (!adding) {
                builder.setNeutralButton("删除", (dialog, which) -> {
                    if (!editable()) return;
                    if (!sameConfig(config)) return;
                    List<Config.Point> list = new ArrayList<>(config.points);
                    list.remove(row);
                    commit(config.withPoints(list, config.width, config.height, config.rotation));
                });
            }
            saveDialog(builder, () -> {
                if (!sameConfig(config)) throw new IllegalArgumentException("点位已变化，请重新打开编辑。");
                ClickService.Screen screen = screen();
                if ((config.points.size() > 1 || (adding && !config.points.isEmpty())) && !screen.matches(config))
                    throw new IllegalArgumentException("屏幕布局已变更，请在原方向编辑，或清空旧点位后重录。");
                Config.Point p = new Config.Point(old.name, value(x, 0, screen.width - 1),
                    value(y, 0, screen.height - 1), value(count, 1, 1000),
                    enabled.isChecked(), doubleTap.isChecked());
                List<Config.Point> list = new ArrayList<>(config.points);
                if (adding) list.add(p);
                else list.set(row, p);
                commit(config.withPoints(list, screen.width, screen.height, screen.rotation));
            });
        } catch (IllegalArgumentException | IllegalStateException e) { toast(e.getMessage()); }
    }

    private void shift(int from, int direction) {
        if (!editable()) return;
        Config config = store.load();
        int to = from + direction;
        if (from >= config.points.size() || to < 0 || to >= config.points.size()) { render(); return; }
        List<Config.Point> list = new ArrayList<>(config.points);
        Collections.swap(list, from, to);
        commit(config.withPoints(list, config.width, config.height, config.rotation));
    }

    private void editTiming() {
        if (!editable()) return;
        Config config = store.load();
        LinearLayout form = form();
        form.addView(text("循环模式", 14, GRAY, false));
        Spinner modes = new Spinner(this);
        ArrayAdapter<String> choices = new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item,
            new String[] { "无限循环", "指定轮数", "指定运行时间" });
        modes.setAdapter(choices);
        modes.setSelection(config.mode.ordinal());
        form.addView(modes);
        EditText point = number(form, "相邻点间隔（0–1000 ms）", config.pointMs);
        EditText round = number(form, "轮间隔（0–5000 ms）", config.roundMs);
        EditText delay = number(form, "启动倒计时（0–30 秒）", config.countdown);
        EditText rounds = number(form, "指定轮数", config.rounds);
        EditText time = number(form, "指定运行时间（1–86400 秒）", config.seconds);
        saveDialog(formDialog("运行参数", form), () -> {
            Config latest = store.load();
            commit(new Config(latest.points, value(point, 0, 1000), value(round, 0, 5000),
                value(delay, 0, 30), Config.Mode.values()[modes.getSelectedItemPosition()],
                value(rounds, 1, Integer.MAX_VALUE), value(time, 1, 86400),
                latest.width, latest.height, latest.rotation));
        });
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (result != RESULT_OK || data == null || data.getData() == null) return;
        try {
            if (request == EXPORT) {
                try (OutputStream stream = getContentResolver().openOutputStream(data.getData(), "wt")) {
                    if (stream == null) throw new IllegalStateException("无法打开目标文件。");
                    stream.write(Store.encode(store.load()).getBytes(StandardCharsets.UTF_8));
                }
                toast("配置已导出");
            } else if (request == IMPORT && editable()) {
                try (InputStream stream = getContentResolver().openInputStream(data.getData())) {
                    if (stream == null) throw new IllegalStateException("无法读取配置。");
                    ByteArrayOutputStream content = new ByteArrayOutputStream();
                    byte[] buffer = new byte[4096];
                    int bytes;
                    while ((bytes = stream.read(buffer)) != -1) {
                        if (content.size() + bytes > 65536) throw new IllegalArgumentException("配置文件超过 64 KB。");
                        content.write(buffer, 0, bytes);
                    }
                    commit(Store.decode(new String(content.toByteArray(), StandardCharsets.UTF_8)));
                }
                toast("配置已导入");
            }
        } catch (Exception e) { toast("配置操作失败。 " + e.getMessage()); }
    }

    private ClickService.Screen screen() {
        android.view.Display display = getWindowManager().getDefaultDisplay();
        android.graphics.Point size = new android.graphics.Point();
        display.getRealSize(size);
        return new ClickService.Screen(size.x, size.y, display.getRotation());
    }

    private boolean sameConfig(Config expected) {
        boolean same = Store.encode(expected).equals(Store.encode(store.load()));
        if (!same) render();
        return same;
    }

    private boolean editable() {
        ClickService service = ClickService.current;
        if (service != null && service.busy()) {
            toast("请先用悬浮栏停止当前任务。");
            return false;
        }
        return true;
    }

    private void commit(Config config) { store.save(config); render(); }

    private LinearLayout section(int color) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(14), dp(12), dp(14), dp(13));
        GradientDrawable background = new GradientDrawable();
        background.setColor(color);
        background.setCornerRadius(dp(14));
        panel.setBackground(background);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.bottomMargin = dp(12);
        body.addView(panel, params);
        return panel;
    }

    private LinearLayout horizontal(LinearLayout parent) {
        LinearLayout line = new LinearLayout(this);
        line.setGravity(Gravity.CENTER_VERTICAL);
        parent.addView(line);
        return line;
    }

    private TextView text(String value, int size, int color, boolean bold) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextColor(color);
        view.setTextSize(size);
        view.setPadding(0, dp(3), 0, dp(4));
        if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    private Button makeButton(String label, int background, int color) {
        Button button = new Button(this);
        button.setAllCaps(false);
        button.setText(label);
        button.setTextSize(13);
        button.setTextColor(color);
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setPadding(dp(8), 0, dp(8), 0);
        button.setBackgroundTintList(ColorStateList.valueOf(background));
        return button;
    }

    private Button action(LinearLayout group, String label, int color, Runnable click) {
        Button button = makeButton(label, color, color == BLUE ? Color.WHITE : INK);
        button.setOnClickListener(view -> click.run());
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, dp(48));
        params.rightMargin = dp(4);
        group.addView(button, params);
        return button;
    }

    private LinearLayout form() {
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(20), dp(10), dp(20), dp(8));
        return form;
    }

    private AlertDialog.Builder formDialog(String title, View content) {
        ScrollView scroll = new ScrollView(this);
        scroll.addView(content);
        return new AlertDialog.Builder(this).setTitle(title).setView(scroll).setNegativeButton("取消", null)
            .setPositiveButton("保存", null);
    }

    private void saveDialog(AlertDialog.Builder builder, Runnable save) {
        AlertDialog dialog = builder.show();
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            if (!editable()) return;
            try { save.run(); dialog.dismiss(); }
            catch (IllegalArgumentException e) { toast(e.getMessage()); }
        });
    }

    private EditText number(LinearLayout parent, String title, int initial) {
        parent.addView(text(title, 14, GRAY, false));
        EditText field = new EditText(this);
        field.setInputType(InputType.TYPE_CLASS_NUMBER);
        field.setSingleLine();
        field.setText(String.valueOf(initial));
        field.setContentDescription(title);
        parent.addView(field, new LinearLayout.LayoutParams(-1, -2));
        return field;
    }

    private CheckBox check(LinearLayout parent, String title, boolean selected) {
        CheckBox check = new CheckBox(this);
        check.setText(title);
        check.setChecked(selected);
        parent.addView(check);
        return check;
    }

    private int value(EditText input, int min, int max) {
        try {
            int n = Integer.parseInt(input.getText().toString().trim());
            if (n >= min && n <= max) return n;
        } catch (NumberFormatException ignored) { }
        input.requestFocus();
        throw new IllegalArgumentException(input.getContentDescription() + "请输入 " + min + "–" + max + " 之间的整数。");
    }

    private void toast(String value) { Toast.makeText(this, value, Toast.LENGTH_SHORT).show(); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}

