package io.clipbridge.android;

import android.app.Application;
import android.content.Context;
import de.robv.android.xposed.IXposedHookLoadPackage;
import de.robv.android.xposed.XC_MethodHook;
import de.robv.android.xposed.XposedHelpers;
import de.robv.android.xposed.callbacks.XC_LoadPackage;
import java.util.concurrent.atomic.AtomicBoolean;

public final class Hook implements IXposedHookLoadPackage {
    private static final AtomicBoolean started=new AtomicBoolean();
    public void handleLoadPackage(XC_LoadPackage.LoadPackageParam p) throws Throwable {
        if(!p.packageName.equals("com.android.systemui") || !p.processName.equals("com.android.systemui")) return;
        XposedHelpers.findAndHookMethod(Application.class,"attach",Context.class,new XC_MethodHook(){
            protected void afterHookedMethod(MethodHookParam param){
                if(started.compareAndSet(false,true)){
                    Context context=(Context)param.args[0];
                    new android.os.Handler(android.os.Looper.getMainLooper()).post(()->{
                        try{new Engine(context).start();}catch(Throwable ignored){started.set(false);}
                    });
                }
            }
        });
    }
}
