# MiMotionSign

## 一、Fork 仓库

## 二、添加 Secret 和 Variable

**`Settings`-->`Secrets and variables`-->`Actions`-->`Secrets`-->`New repository secret`，添加以下secrets：**
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
		"UseConcurrent": false,//是否使用并行执行。true=并行执行，false=顺序执行
		"Sleep_Gap_Second": 6,//UseConcurrent=false时生效。顺序执行时的间隔秒数
 		"MaxTaskDurationSeconds": 120,//UseConcurrent=true时生效。每个任务执行的最大时长，单位：秒
 		"ConsumerCount": 4,//UseConcurrent=true时生效。并行执行的最大任务数
		"Peoples": [{
			"User": "xxx@qq.com",
			"Pwd": "xxxx",
			"MinStep": 20000,
			"MaxStep": 30000
		}]
	}
    ```

**`Settings`-->`Secrets and variables`-->`Actions`-->`Variables`-->`New repository variable`，添加以下variables：**
- `CRON_HOURS`：
Cron定时的小时，示例值：2,4,6,10,14,22。详情参见下面的 任务执行时间


## 三、运行

**`Actions`->`Run`->`Run workflow`**

## 四、查看运行结果

**`Actions`->`Run`->`build`**

## 五、任务执行时间

目前仓库代码已经调整为方式2

### 方式1、编辑 .github/workflows/run.yml 中的cron表达式
cron表达式格式如下: 分 时 日 月 年

任务 .github/workflows/cron.yml 在每次执行任务后会随机生成分钟数

```
jobs:
repo-sync:
	runs-on: ubuntu-latest
	timeout-minutes: 3
	if: github.event.workflow_run.conclusion == 'success'
	steps:
	- uses: actions/checkout@v2
		with:
		token: ${{ secrets.PAT }}
	- name: random cron
		run: |
		sed -i -E "s/(- cron: ')[0-9]+( [^[:space:]]+ \* \* \*')/\1$(($RANDOM % 59))\2/g" .github/workflows/run.yml
		git config user.name github-actions
		git config user.email github-actions@github.com
		git add .
		git commit -m "random cron"
		git push origin main
```

目前的2个问题

(1)随机的新分钟数，可能与当前cron中的分钟数一致，导致cron.yml执行到push时会报错，因为git未检测到改动内容

(2)当前小时内重复执行，例如：8:05分执行后，分钟值随机为50，则会在8:50再次执行

### 方式2、完善随机数逻辑
逻辑来自：https://github.com/TonyJiangWJ/mimotion

添加名为 `CRON_HOURS` 的repository variable，示例值：0,2,4,6,8,10,12,14,22

添加 cron_change_time 和 cron_convert.sh 2个文件

调整 .github/workflows/cron.yml 的内容

```
jobs:
repo-sync:
	runs-on: ubuntu-latest
	timeout-minutes: 3
	if: github.event.workflow_run.conclusion == 'success' || github.event_name == 'workflow_dispatch'
	steps:
	- uses: actions/checkout@v3
		with:
		token: ${{ secrets.PAT }}
	- name: random cron
		run: |
		source cron_convert.sh
		echo "configed CRON_HOURS ${{ vars.CRON_HOURS }}"
		persist_execute_log ${{ github.event_name }} ${{ vars.CRON_HOURS }}
		git config user.name github-actions
		git config user.email github-actions@users.noreply.github.com
		git add .
		current=`TZ=Asia/Shanghai date '+%Y-%m-%d %H:%M:%S'`
		git commit -m "[${current}] random cron trigger by ${{ github.event_name }}"
		git push origin main
```

调整后：

1、当前小时内不会重复执行

8:05分执行后，将从小时中剔除8，即8:00-8:59都不会再重复执行

## 六、执行逻辑

1、执行逻辑来自于

https://github.com/geoisam/FuckScripts/blob/1002fc17e28e0d9f5f83af80e62cbd0c57fb47eb/zepplife.py

2、批次逻辑来自于

https://github.com/Li-safe/mimotion/blob/master/.github/workflows/run.yml

3、关键逻辑点

同一个IP在同一小时内，最多执行4个账号

(1)方案1，创建多个仓库，每个仓库处理4个账号

(2)方案2，每次执行4个账号，延迟1小时后，再继续执行后续的账号

当前使用方案2，多批次执行，代码逻辑每次处理3个账号，并非4个。

