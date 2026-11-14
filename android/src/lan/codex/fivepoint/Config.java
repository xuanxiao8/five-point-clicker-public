package lan.codex.fivepoint;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

final class Config {
    static final int MAX_POINTS = 5;
    enum Mode { INFINITE, ROUNDS, TIME }

    static final class Point {
        final String name;
        final int x, y, repeat;
        final boolean enabled, doubleTap;

        Point(String name, int x, int y, int repeat, boolean enabled, boolean doubleTap) {
            if (name == null || !name.matches("[A-E]") || x < 0 || y < 0 || repeat < 1 || repeat > 1000) {
                throw new IllegalArgumentException("点位编号、坐标或次数无效。次数范围为 1–1000。");
            }
            this.name = name;
            this.x = x;
            this.y = y;
            this.repeat = repeat;
            this.enabled = enabled;
            this.doubleTap = doubleTap;
        }
    }

    final List<Point> points;
    final int pointMs, roundMs, countdown, rounds, seconds;
    final int width, height, rotation;
    final Mode mode;

    Config(List<Point> points, int pointMs, int roundMs, int countdown, Mode mode,
            int rounds, int seconds, int width, int height, int rotation) {
        if (points.size() > MAX_POINTS || points.stream().map(p -> p.name).distinct().count() != points.size()
                || pointMs < 0 || pointMs > 1000 || roundMs < 0 || roundMs > 5000
                || countdown < 0 || countdown > 30 || rounds < 1 || seconds < 1 || seconds > 86400
                || width < 0 || height < 0 || rotation < 0 || rotation > 3) {
            throw new IllegalArgumentException("配置超出范围或包含重复编号。");
        }
        this.points = Collections.unmodifiableList(new ArrayList<>(points));
        this.pointMs = pointMs;
        this.roundMs = roundMs;
        this.countdown = countdown;
        this.mode = mode;
        this.rounds = rounds;
        this.seconds = seconds;
        this.width = width;
        this.height = height;
        this.rotation = rotation;
    }

    static Config defaults() {
        return new Config(Collections.emptyList(), 25, 40, 3, Mode.INFINITE, 100, 60, 0, 0, 0);
    }

    Config withPoints(List<Point> list, int width, int height, int rotation) {
        return new Config(list, pointMs, roundMs, countdown, mode, rounds, seconds, width, height, rotation);
    }

    String freeName() {
        for (char name = 'A'; name < 'A' + MAX_POINTS; ++name) {
            String candidate = String.valueOf(name);
            if (points.stream().noneMatch(point -> point.name.equals(candidate))) return candidate;
        }
        throw new IllegalStateException("最多添加 5 个位置。");
    }
}
