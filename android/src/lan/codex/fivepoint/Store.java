package lan.codex.fivepoint;

import android.content.Context;
import android.content.SharedPreferences;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.List;

final class Store {
    private final SharedPreferences prefs;

    Store(Context context) {
        prefs = context.getSharedPreferences("clicker", Context.MODE_PRIVATE);
    }

    Config load() {
        String data = prefs.getString("configuration", null);
        if (data == null) return Config.defaults();
        try { return decode(data); }
        catch (Exception e) { return Config.defaults(); }
    }

    void save(Config config) {
        prefs.edit().putString("configuration", encode(config)).apply();
    }

    static String encode(Config config) {
        try {
            JSONArray points = new JSONArray();
            for (Config.Point p : config.points) {
                points.put(new JSONObject().put("name", p.name).put("x", p.x).put("y", p.y)
                    .put("repeat", p.repeat).put("enabled", p.enabled).put("doubleTap", p.doubleTap));
            }
            return new JSONObject().put("version", 1).put("points", points)
                .put("pointMs", config.pointMs).put("roundMs", config.roundMs)
                .put("countdown", config.countdown).put("rounds", config.rounds)
                .put("seconds", config.seconds).put("mode", config.mode.name())
                .put("width", config.width).put("height", config.height)
                .put("rotation", config.rotation).toString(2);
        } catch (JSONException e) {
            throw new IllegalStateException("生成 JSON 失败。", e);
        }
    }

    static Config decode(String json) throws JSONException {
        JSONObject object = new JSONObject(json);
        if (object.getInt("version") != 1) throw new JSONException("不支持此配置版本。");
        JSONArray array = object.getJSONArray("points");
        if (array.length() > Config.MAX_POINTS) throw new JSONException("点位超过 5 个。");
        List<Config.Point> points = new ArrayList<>();
        for (int i = 0; i < array.length(); i++) {
            JSONObject p = array.getJSONObject(i);
            points.add(new Config.Point(p.getString("name"), p.getInt("x"), p.getInt("y"),
                p.getInt("repeat"), p.getBoolean("enabled"), p.getBoolean("doubleTap")));
        }
        return new Config(points, object.getInt("pointMs"), object.getInt("roundMs"),
            object.getInt("countdown"), Config.Mode.valueOf(object.getString("mode")),
            object.getInt("rounds"), object.getInt("seconds"), object.getInt("width"),
            object.getInt("height"), object.getInt("rotation"));
    }
}
