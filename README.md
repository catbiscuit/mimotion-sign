# MiMotionSign

## 一、Fork 仓库

## 二、添加 Secret

**`Settings`->`Secrets`->`New secret`，添加以下Secret：**
- `PAT`：
GitHub的token

- `CONF`：其值如下：
    
	```json
	{
		"Bark_Devicekey": "xxx",
		"Bark_Icon": "https://xxx/logo_2x.png",
		"Smtp_Server": "smtp.qq.com",
		"Smtp_Port": 587,
		"Smtp_Email": "xxx@qq.com",
		"Smtp_Password": "xxxx",
		"Receive_Email_List": [
			"xxx@qq.com"
		],
		"Sleep_Gap_Second": 6,
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

