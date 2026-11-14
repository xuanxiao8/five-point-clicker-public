package lan.codex.touchreceiver;
import android.app.Activity;
import android.os.Bundle;
import android.util.Log;
import android.view.MotionEvent;
import android.view.View;
import android.widget.TextView;

public final class Receiver extends Activity {
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        TextView view = new TextView(this);
        view.setText("FivePoint Android integration test receiver");
        view.setTextSize(22);
        view.setPadding(20, 50, 20, 0);
        view.setBackgroundColor(0xfff2f6ff);
        view.setOnTouchListener((v, event) -> {
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_CANCEL)
                Log.i("FiveTouch", event.getEventTime() + " " + action + " " + event.getRawX() + " " + event.getRawY());
            return true;
        });
        setContentView(view);
    }
}
