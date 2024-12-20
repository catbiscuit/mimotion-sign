# MiMotionSign

## 一、Fork 仓库

## 二、添加 Secret

**`Settings`->`Secrets`->`New secret`，添加以下Secret：**
- `PAT`：
GitHub的token

- `CONF`：其值如下：
    
	```json
	{
		"Bark_Devicekey": "xxx",//Bark推送，不使用的话填空
		"Bark_Icon": "https://xxx/logo_2x.png",//Bark推送的icon
		"Smtp_Server": "smtp.qq.com",
		"Smtp_Port": 587,
		"Smtp_Email": "xxx@qq.com",//Email推送，发送者的邮箱，不使用的话填空
		"Smtp_Password": "xxxx",
		"Receive_Email_List": [//Email推送接收者列表，为空时不发送
			"xxx@qq.com"
		],
		"UseConcurrent": false,//是否并行运行
		"Sleep_Gap_Second": 6,//顺序执行时的间隔秒数，UseConcurrent=true时生效
		"Peoples": [{
			"User": "xxx@qq.com",
			"Pwd": "xxxx",
			"MinStep": 20000,
			"MaxStep": 30000
		}]
	}
    ```

## 三、运行

**`Actions`->`Run`->`Run workflow`**

## 四、查看运行结果

**`Actions`->`Run`->`build`**

