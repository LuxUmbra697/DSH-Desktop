/**
 * Example DSH host plugin shipped as a launcher plugin.
 *
 * It registers two model-facing tools. With no `serviceUrl` configured it answers
 * from demo data, so the plugin can be exercised offline; point `serviceUrl` at a
 * real backend to use it as a template for a production integration.
 *
 * The launcher links the bundled dependency tree into ./node_modules (see
 * `dshLinkModules` in launcher.json), which is what makes these bare imports
 * resolvable without installing anything.
 */
import z from '@deepseek-ai/schemastery'
import { defineTool } from '@deepseek-ai/dsh-tools'

export const name = 'myapp-tools'
export const inject = ['tools']
export const Config = z.object({
  serviceUrl: z.string().default(''),
  apiToken: z.string().default('')
})

const DEMO_TICKETS = {
  'TCK-10231': {
    id: 'TCK-10231',
    title: '导出报表在筛选后为空',
    status: 'open',
    priority: 'high',
    summary: '用户选择“本月”筛选后导出 CSV 只有表头，去掉筛选恢复正常。'
  },
  'TCK-10232': {
    id: 'TCK-10232',
    title: '登录后偶发跳回首页',
    status: 'pending',
    priority: 'normal',
    summary: '约 5% 的登录会直接回到首页，刷新后恢复正常会话。'
  }
}

function endpoint(config, path) {
  if (!config.serviceUrl) {
    return null
  }
  return config.serviceUrl.replace(/\/+$/, '') + path
}

async function lookupTicket(config, ticketId) {
  const url = endpoint(config, '/tickets/' + encodeURIComponent(ticketId))
  if (!url) {
    const demo = DEMO_TICKETS[ticketId.toUpperCase()]
    if (!demo) {
      throw new Error('未配置 serviceUrl，且演示数据里没有工单 ' + ticketId)
    }
    return demo
  }
  const response = await fetch(url, {
    headers: config.apiToken ? { authorization: 'Bearer ' + config.apiToken } : {}
  })
  if (!response.ok) {
    throw new Error('工单服务返回 ' + response.status + '：' + ticketId)
  }
  return await response.json()
}

/**
 * Registers the ticket tools.
 * @param ctx - registrant context carrying the tool registry.
 * @param config - deployment configuration with the optional backend URL.
 */
export function apply(ctx, config) {
  ctx.tools.register(
    defineTool({
      name: 'myapp_lookup_ticket',
      description:
        'Read one ticket from MyApp. Always call it before answering a question about a specific ticket.',
      parameters: {
        ticketId: {
          type: 'string',
          required: true,
          description: 'Ticket id such as TCK-10231.'
        }
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            id: { type: 'string', required: true },
            title: { type: 'string', required: true },
            status: { type: 'string', required: true },
            summary: { type: 'string', required: true }
          }
        },
        render: (_args, value) => [
          { type: 'text', text: value.id + ' [' + value.status + '] ' + value.title + '\n' + value.summary }
        ]
      },
      async execute(args) {
        const ticket = await lookupTicket(config, args.ticketId)
        return {
          id: String(ticket.id),
          title: String(ticket.title),
          status: String(ticket.status),
          summary: String(ticket.summary)
        }
      },
      presentCall: (args) => ({
        card: 'generic',
        title: '读取工单 ' + args.ticketId,
        kind: 'other',
        rawInput: args
      })
    })
  )

  ctx.tools.register(
    defineTool({
      name: 'myapp_ticket_playbook',
      description:
        'Return the handling checklist for a ticket category. Use it to structure a reply before writing it.',
      parameters: {
        category: {
          type: 'string',
          required: true,
          enum: ['export', 'login', 'billing', 'other'],
          description: 'Ticket category.'
        }
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            category: { type: 'string', required: true },
            steps: { type: 'array', required: true, items: { type: 'string' } }
          }
        },
        render: (_args, value) => [
          { type: 'text', text: value.steps.map((step, index) => index + 1 + '. ' + step).join('\n') }
        ]
      },
      execute(args) {
        const playbooks = {
          export: ['确认筛选条件与导出行数', '检查后端导出任务日志', '用同样条件复现一次'],
          login: ['索取时间点与地域', '比对网关会话日志', '确认是否存在多标签页冲突'],
          billing: ['核对账单周期', '确认支付回调状态', '必要时发起人工对账'],
          other: ['先复述用户现象', '收集环境信息', '给出下一步时间承诺']
        }
        return Promise.resolve({ category: args.category, steps: playbooks[args.category] })
      },
      presentCall: (args) => ({
        card: 'generic',
        title: '读取处理手册：' + args.category,
        kind: 'other',
        rawInput: args
      })
    })
  )

  // Boot marker: the launcher and the smoke test assert this line to prove the
  // plugin was imported and applied, not merely listed in the composed tree.
  console.log('[myapp-tools] applied: registered myapp_lookup_ticket, myapp_ticket_playbook'
    + (config.serviceUrl ? ' (backend: ' + config.serviceUrl + ')' : ' (demo data)'))
}
