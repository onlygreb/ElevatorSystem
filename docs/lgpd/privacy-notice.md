# Privacy notice (aviso de privacidade)

> [!IMPORTANT]
> System documentation draft for the data controller (controlador) and their legal counsel to
> validate. Not legal advice.
>
> **For implementers.** This is the text of the UI's privacy page, in en-US and pt-BR. The page
> shows the half that matches the current culture. Every `{{PLACEHOLDER}}` is replaced from
> configuration at run time; the page must refuse to render a placeholder literally in
> production. Keep both halves in step: a change to one is a change to the other, and to the
> [ROPA](ropa.md) or the [retention schedule](retention.md) if it changes a fact.

Configuration values used below:

| Placeholder | Meaning |
|---|---|
| `{{CONTROLLER_LEGAL_NAME}}`, `{{CONTROLLER_CNPJ}}`, `{{CONTROLLER_ADDRESS}}` | The controller (controlador) |
| `{{DPO_NAME}}`, `{{DPO_EMAIL}}` | The data protection officer (encarregado) |
| `{{HOSTING_PROVIDER}}`, `{{HOSTING_REGION}}` | Where the data is stored |
| `{{TRANSFER_SAFEGUARD}}` | The Art. 33 mechanism, only if the hosting region is outside Brazil |
| `{{LAST_UPDATED}}` | Date of the last change to this notice |

---

## English (en-US)

### Privacy notice

Last updated: {{LAST_UPDATED}}

This notice explains how the ElevatorSystem elevator control system at this building processes
personal data, in line with the Brazilian General Data Protection Law (LGPD, Law nº 13.709/2018).

### Who is responsible

The controller of your personal data is **{{CONTROLLER_LEGAL_NAME}}**, CNPJ {{CONTROLLER_CNPJ}},
{{CONTROLLER_ADDRESS}}.

Our data protection officer (encarregado) is **{{DPO_NAME}}**, who you can contact at
**{{DPO_EMAIL}}** about anything in this notice.

### If you use the elevator or the passenger screen

**We do not collect personal data about you.** A call to the elevator records only the floor, the
direction and the time. It is never linked to your name, badge, phone, device or IP address.

To protect the service from abuse, your IP address is used for a moment to limit how many
requests a single source can send. It is kept only in memory for that purpose and is never
stored.

### If you are a member of staff with access to the operations panel

| What we process | Why | Legal basis (LGPD Art. 7) | How long we keep it |
|---|---|---|---|
| Your name, work e-mail, role, password (stored only as a secure hash) and two-factor authentication data | To give you access to the operations panel and limit what you can do to your role | Performance of your employment or service contract (V) or, if you work for a contractor, our legitimate interest in operating the building safely (IX) | While your account is active, plus 6 months after it is closed |
| A record of the privileged actions you perform (for example emergency stop, maintenance mode, user changes), with the time and a protected code derived from your IP address (never the address itself) | Security and accountability for a safety-related system | Our legitimate interest in the security of the system (IX), which also serves our duty to protect data (Art. 46) | 1 year |

We do not use this data for any other purpose. In particular, the action record is not used to
evaluate your performance or to monitor your working hours.

**Who can see it.** Administrators can see staff accounts. Auditors and administrators can see the
action record. We do not sell or share your data with anyone else, except service providers that
store it on our behalf ({{HOSTING_PROVIDER}}) and authorities when the law requires it.

**Where it is stored.** {{HOSTING_PROVIDER}}, in {{HOSTING_REGION}}. {{TRANSFER_SAFEGUARD}}

**How we protect it.** Encrypted connections, passwords stored only as secure hashes,
two-factor authentication, encryption of authentication secrets, access limited by role, and a
tamper-evident record of privileged actions. Our technical logs are designed to contain no
personal data.

### Your rights

Under Art. 18 of the LGPD you can ask us to:

- confirm whether we process your personal data, and give you access to it
- correct incomplete, inaccurate or outdated data
- anonymise, block or delete data that is unnecessary, excessive or processed unlawfully
- give you your data in a structured format (portability)
- delete your data
- tell you who we have shared your data with
- review a decision taken solely by automated means (we do not take any such decisions; account
  lockout after repeated failed sign-ins is a temporary security measure that an administrator
  can lift)

You can also object to processing based on our legitimate interest if you believe it does not
comply with the LGPD.

**How to exercise them.**

- **In the operations panel**, under your profile: **Export my data** downloads everything we hold
  about you as a JSON file, and **Delete my data** asks us to delete your account. When it is
  deleted, the actions you performed stay in the record so that it remains tamper-evident, but
  nothing links them to you any more.
- **By e-mail** to our encarregado at {{DPO_EMAIL}}, for anything else or if you no longer have
  access to the panel. We may ask you to confirm your identity before we act.

We confirm whether we hold your data straight away, and give you a complete answer within
15 days (Art. 19). If the law allows or requires us to keep some data for longer (Art. 16), we
will tell you what and why.

If you are not satisfied with our answer, you can complain to the National Data Protection
Authority (ANPD).

### Cookies and browser storage

We use **only what is strictly necessary** for the system to work, so we do not ask for your
consent and there is no cookie banner.

| What | Purpose | Who gets it | Duration |
|---|---|---|---|
| Authentication cookie | Keeps you signed in to the operations panel | Staff who sign in | Ends after 20 minutes without activity, or when you sign out |
| Antiforgery cookie | Protects your actions against cross-site request forgery | Staff who sign in | Your browser session |
| Language preference (browser storage) | Shows the site in the language you chose | Anyone who chooses a language | Until you clear it in your browser |

We do not use analytics, advertising or third-party cookies.

### Changes to this notice

When we change this notice, we update the date at the top. If a change affects how we use your
personal data, staff will be told in the operations panel before it takes effect.

---

## Português (pt-BR)

### Aviso de privacidade

Última atualização: {{LAST_UPDATED}}

Este aviso explica como o sistema de controle de elevador ElevatorSystem deste edifício trata
dados pessoais, de acordo com a Lei Geral de Proteção de Dados Pessoais (LGPD, Lei nº 13.709/2018).

### Quem é o responsável

O controlador dos seus dados pessoais é **{{CONTROLLER_LEGAL_NAME}}**, CNPJ {{CONTROLLER_CNPJ}},
{{CONTROLLER_ADDRESS}}.

Nosso encarregado pelo tratamento de dados pessoais é **{{DPO_NAME}}**, que pode ser contatado em
**{{DPO_EMAIL}}** sobre qualquer assunto deste aviso.

### Se você usa o elevador ou a tela de passageiros

**Não coletamos dados pessoais sobre você.** Uma chamada ao elevador registra apenas o andar, o
sentido e o horário. Ela nunca é associada ao seu nome, crachá, telefone, dispositivo ou endereço IP.

Para proteger o serviço contra abusos, o seu endereço IP é usado por um instante para limitar
quantas solicitações uma mesma origem pode enviar. Ele fica apenas em memória para essa
finalidade e nunca é armazenado.

### Se você faz parte da equipe com acesso ao painel de operações

| O que tratamos | Para quê | Base legal (LGPD, art. 7º) | Por quanto tempo guardamos |
|---|---|---|---|
| Seu nome, e-mail corporativo, perfil de acesso, senha (armazenada apenas como hash seguro) e dados de autenticação em dois fatores | Dar a você acesso ao painel de operações e limitar o que você pode fazer ao seu perfil | Execução do seu contrato de trabalho ou de prestação de serviços (inciso V) ou, se você trabalha para uma empresa contratada, nosso legítimo interesse em operar o edifício com segurança (inciso IX) | Enquanto sua conta estiver ativa, e mais 6 meses após o encerramento |
| Um registro das ações privilegiadas que você realiza (por exemplo, parada de emergência, modo de manutenção, alterações de usuários), com o horário e um código protegido derivado do seu endereço IP (nunca o próprio endereço) | Segurança e prestação de contas de um sistema relacionado à segurança | Nosso legítimo interesse na segurança do sistema (inciso IX), que também atende ao nosso dever de proteger os dados (art. 46) | 1 ano |

Não usamos esses dados para nenhuma outra finalidade. Em especial, o registro de ações não é
usado para avaliar o seu desempenho nem para controlar o seu horário de trabalho.

**Quem tem acesso.** Administradores podem ver as contas da equipe. Auditores e administradores
podem ver o registro de ações. Não vendemos nem compartilhamos seus dados com mais ninguém, exceto
com prestadores de serviço que os armazenam em nosso nome ({{HOSTING_PROVIDER}}) e com autoridades
quando a lei exigir.

**Onde ficam armazenados.** {{HOSTING_PROVIDER}}, em {{HOSTING_REGION}}. {{TRANSFER_SAFEGUARD}}

**Como protegemos.** Conexões criptografadas, senhas armazenadas apenas como hash seguro,
autenticação em dois fatores, criptografia dos segredos de autenticação, acesso limitado por
perfil e um registro de ações privilegiadas à prova de adulteração. Nossos registros técnicos
(logs) são projetados para não conter dados pessoais.

### Seus direitos

Pelo art. 18 da LGPD, você pode nos pedir para:

- confirmar se tratamos seus dados pessoais e dar acesso a eles
- corrigir dados incompletos, inexatos ou desatualizados
- anonimizar, bloquear ou eliminar dados desnecessários, excessivos ou tratados em desconformidade
  com a lei
- fornecer seus dados em formato estruturado (portabilidade)
- eliminar seus dados
- informar com quem compartilhamos seus dados
- revisar uma decisão tomada unicamente com base em tratamento automatizado (não tomamos decisões
  desse tipo; o bloqueio da conta após tentativas de acesso malsucedidas é uma medida de segurança
  temporária que um administrador pode desfazer)

Você também pode se opor a um tratamento baseado no nosso legítimo interesse se entender que ele
não cumpre a LGPD.

**Como exercer seus direitos.**

- **No painel de operações**, no seu perfil: **Exportar meus dados** baixa tudo o que temos sobre
  você em um arquivo JSON, e **Excluir meus dados** solicita a exclusão da sua conta. Após a
  exclusão, as ações que você realizou permanecem no registro para que ele continue à prova de
  adulteração, mas nada mais as associa a você.
- **Por e-mail** ao nosso encarregado, em {{DPO_EMAIL}}, para qualquer outro pedido ou se você não
  tiver mais acesso ao painel. Podemos pedir que você confirme sua identidade antes de atendermos.

Confirmamos imediatamente se temos dados seus e enviamos uma resposta completa em até 15 dias
(art. 19). Se a lei permitir ou exigir que guardemos alguns dados por mais tempo (art. 16),
informaremos quais e por quê.

Se não estiver satisfeito com a nossa resposta, você pode apresentar reclamação à Autoridade
Nacional de Proteção de Dados (ANPD).

### Cookies e armazenamento no navegador

Usamos **apenas o estritamente necessário** para o funcionamento do sistema. Por isso não pedimos
o seu consentimento e não exibimos aviso de cookies.

| O quê | Finalidade | Quem recebe | Duração |
|---|---|---|---|
| Cookie de autenticação | Mantém você conectado ao painel de operações | Equipe que faz login | Expira após 20 minutos sem atividade ou quando você sai |
| Cookie antifalsificação | Protege suas ações contra falsificação de solicitações entre sites (CSRF) | Equipe que faz login | Sessão do navegador |
| Preferência de idioma (armazenamento do navegador) | Exibe o site no idioma que você escolheu | Quem escolhe um idioma | Até você apagá-la no seu navegador |

Não usamos cookies de análise, de publicidade ou de terceiros.

### Alterações deste aviso

Quando alterarmos este aviso, atualizaremos a data no início. Se uma alteração afetar a forma como
usamos seus dados pessoais, a equipe será avisada no painel de operações antes de ela entrar em vigor.

---

## Revision history

| Date | Change | Reviewed by |
|---|---|---|
| 2026-10-19 | First draft of both language versions | Engineering; pending controller and counsel review |
