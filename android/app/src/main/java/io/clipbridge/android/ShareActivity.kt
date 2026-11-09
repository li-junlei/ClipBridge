package io.clipbridge.android

import androidx.activity.compose.setContent
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import top.yukonga.miuix.kmp.basic.*
import top.yukonga.miuix.kmp.theme.*

class ShareActivity:ShareActivityBase(){
    private var status by mutableStateOf("")
    private var sending by mutableStateOf(false)
    override fun updateShare(text:String,busy:Boolean){status=text;sending=busy}
    override fun showShare(){setContent{
        val dark=isSystemInDarkTheme()
        val theme=remember(dark){ThemeController(colorSchemeMode=if(dark)ColorSchemeMode.Dark else ColorSchemeMode.Light)}
        MiuixTheme(controller=theme){
            var target by remember{mutableIntStateOf(0)}
            var original by remember{mutableStateOf(false)}
            val color=MiuixTheme.colorScheme
            Scaffold(topBar={TopAppBar(title="发送文件",largeTitle="发送文件")}){padding->
                Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal=20.dp),verticalArrangement=Arrangement.spacedBy(16.dp)){
                    Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(22.dp)){
                        Text("已选择 ${itemCount} 项",fontSize=22.sp,fontWeight=FontWeight.Bold)
                        Spacer(Modifier.height(10.dp))
                        Text("单文件上限 100 MB · 对方同意后传输",fontSize=13.sp,color=color.onSurface.copy(alpha=.6f))
                    }}
                    Text("接收设备",fontSize=14.sp,color=color.onSurface.copy(alpha=.6f),modifier=Modifier.padding(start=4.dp))
                    Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){for((index,name) in targetNames.withIndex()){
                        BasicComponent(title=name,summary=if(target==index)"已选择" else "点按选择",onClick={if(!sending)target=index})
                    }}
                    Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Row(Modifier.fillMaxWidth().padding(20.dp),verticalAlignment=Alignment.CenterVertically){
                        Column(Modifier.weight(1f)){Text("图片作为文件发送",fontSize=16.sp);Text("保留原始格式，等待接收确认",fontSize=12.sp,color=color.onSurface.copy(alpha=.6f))}
                        Switch(original,{if(!sending)original=it})
                    }}
                    Text("图片默认发送到所有已开启设备；文件发送到所选设备。WebDAV 附件采用定期拉取，可能需要几分钟。",fontSize=13.sp,color=color.onSurface.copy(alpha=.6f),modifier=Modifier.padding(horizontal=4.dp))
                    if(status.isNotBlank())Text(status,fontSize=14.sp)
                    Button(onClick={if(!sending)sendShare(original,target)},modifier=Modifier.fillMaxWidth()){Text(if(sending)"正在准备…"else"发送",fontSize=16.sp)}
                    TextButton(text="取消",onClick={if(!sending)finish()},modifier=Modifier.align(Alignment.CenterHorizontally))
                    Spacer(Modifier.height(20.dp))
                }
            }
        }
    }}
}
