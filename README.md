# VPN Panel

Aplicativo para Windows que deixa a VPN WireGuard fácil de usar: um clique para conectar, lista de servidores, escolha de quais programas passam pela VPN e aviso quando o servidor para de responder. Funciona com o cliente gratuito [WireSock](https://www.wiresock.net/) e com o arquivo `.conf` de qualquer provedor WireGuard (Proton VPN, Mullvad, Surfshark, IVPN...).

**[⬇ Baixar a versão mais recente](https://github.com/JMqz/VPN-Panel/releases/latest)**

---

## Guia de instalação

Você vai precisar de:

- Windows 10 ou 11;
- uma conta em um provedor de VPN que ofereça **WireGuard** (o plano gratuito da Proton VPN serve);
- uns 10 minutos.

### 1. Instale o VPN Panel

1. Abra a [página de downloads](https://github.com/JMqz/VPN-Panel/releases/latest) e baixe o arquivo **`VPN-Panel-Setup-<versão>.exe`** (ou o arquivo **`VPN-Panel-Setup-<versão>.zip`** caso o navegador bloqueie o download do `.exe`).
2. Abra o arquivo baixado (se baixou o `.zip`, extraia o instalador antes).
3. Se aparecer a tela azul **"O Windows protegeu o computador"**, clique em **Mais informações** e depois em **Executar assim mesmo**. O aviso aparece porque o aplicativo não tem assinatura digital paga.
4. O Windows pede permissão de administrador: clique em **Sim**.
5. Escolha se quer o atalho na área de trabalho e clique em **Instalar**.
6. Clique em **Abrir o VPN Panel**.

O aplicativo fica em **Iniciar → VPN Panel**. O Windows só pede permissão na primeira vez que você abre o aplicativo.

### 2. WireSock (automático)

O WireSock é o programa gratuito (para uso pessoal) que faz a conexão. O VPN Panel controla ele por você.

Você não precisa fazer nada: o instalador do VPN Panel já instala o WireSock junto. Se por algum motivo ele não for instalado, o VPN Panel tenta de novo sozinho quando você abrir, e mostra o andamento na tela **"Vamos deixar tudo pronto"**. Se mesmo assim der errado, aparecem os botões **Tentar de novo** e **Baixar no site** ([wiresock.net](https://www.wiresock.net/)).

### 3. Baixe o arquivo do servidor (`.conf`)

Cada provedor gera um arquivo `.conf` com o servidor e a sua chave de acesso. Na primeira tela, o botão **Pegar no Proton VPN** mostra o passo a passo e abre o site certo. **Outro provedor** mostra as instruções gerais.

**Proton VPN** (também funciona no plano grátis; sem conta, crie uma em [account.protonvpn.com](https://account.protonvpn.com/signup?plan=free))
1. Entre em [account.protonvpn.com](https://account.protonvpn.com/downloads).
2. Vá em **Downloads → Configurações do WireGuard**.
3. Escolha a plataforma **Windows** e um servidor. No plano grátis, use os marcados como gratuitos.
4. Clique em **Criar** e depois em **Baixar**.

**Mullvad**
1. Entre em [mullvad.net](https://mullvad.net/account/wireguard-config) com o número da sua conta.
2. Abra o **Gerador de configuração WireGuard** e escolha **Windows**.
3. Gere uma chave, escolha o país e a cidade e baixe o arquivo.

**Outros provedores:** procure por "WireGuard" ou "configuração manual" no site ou no suporte do provedor.

> ⚠️ O arquivo `.conf` funciona como uma senha. Não compartilhe e não publique.

Dica: baixe arquivos de vários países e adicione todos. Assim você troca de servidor com um clique.

### 4. Adicione o servidor no aplicativo

Na maioria das vezes não precisa fazer nada: com o VPN Panel aberto, o arquivo que chega na pasta **Downloads** entra sozinho. Isso vale enquanto você ainda não tem nenhum servidor, e por 20 minutos depois de abrir o passo a passo. Se o arquivo já estava em Downloads antes, a primeira tela mostra **Adicionar este**.

Outras formas:
- **Já tenho o arquivo** (na primeira tela) ou **Servidores → Adicionar**.
- Arrastar o arquivo para a janela.
- Copiar o arquivo (ou o texto da configuração) e apertar **Ctrl+V** na janela.

O VPN Panel aceita `.conf` e também `.zip` com vários `.conf` dentro (como o da Mullvad).

O servidor aparece na lista com a sigla do país e a cidade. Use a barra de pesquisa no topo da aba **Servidores** para filtrar rapidamente por nome, país, cidade ou endereço.

Para saber qual servidor responde mais rápido, clique no botão de medir latência no topo da aba **Servidores**: cada servidor ganha o ping em milissegundos (ex.: `42 ms`), em verde, âmbar ou vermelho. Você também pode clicar no botão **Mais rápido** (ou escolher no menu da bandeja perto do relógio): o aplicativo testa a latência e conecta automaticamente ao servidor mais veloz, sem você precisar clicar de novo. Alguns servidores não respondem a ping e aparecem como "sem ping", o que não impede a conexão.

### 5. Conecte

Na aba **Início**, clique no botão grande:

| O botão fica... | Significa |
|---|---|
| Cinza | Desconectado |
| Girando em âmbar | Conectando ao servidor |
| **Verde** | Conectado e protegido. O servidor confirmou a conexão. |
| Âmbar, "Sem resposta" | O servidor parou de responder. O aplicativo tenta de novo sozinho. |

Abaixo do botão aparece o que está protegido ("Todo o tráfego do PC está protegido") e há quanto tempo você está conectado. Enquanto conectado, a aba **Início** também mostra a velocidade em tempo real (download e upload) e o total transferido na sessão, além do ping do servidor atual. O quadro "Seu IP" mostra o endereço que os sites veem.

Pronto, já dá para usar. Os ajustes abaixo são opcionais.

### 6. Ajuste do seu jeito (opcional)

**Escolher quais programas usam a VPN** (aba **Apps**):
- **Todos:** o computador inteiro passa pela VPN. É o mais seguro e o padrão.
- **Só estes:** só os programas da lista usam a VPN; o resto usa a sua internet normal. Por exemplo, só o navegador ou só o Discord.
- **Todos, exceto:** tudo passa pela VPN, menos os programas da lista. Por exemplo, um jogo que fica lento com VPN.

Para o uso mais comum, o botão **Só o Discord** já deixa tudo pronto: voz, vídeo e transmissão de tela do Discord passam pela VPN, e jogos e o resto usam a sua internet normal. Se a voz ou a transmissão falharem, desligue no Discord a opção **Qualidade de serviço: prioridade alta de pacotes** (Configurações → Voz e vídeo).

**Chamadas e transmissões no Discord (Modo Live):** o aplicativo percebe sozinho quando o Discord entra em chamada ou começa a transmitir e passa a cuidar para não atrapalhar.

- No **Início** aparece um cartão do Discord com a qualidade do caminho pela VPN, medida a cada poucos segundos: ping, oscilação (jitter) e perda de pacotes, mais um selo **Ótima**, **Boa**, **Instável**, **Ruim** ou **Distante** (servidor estável, mas longe: a voz e a imagem chegam atrasadas).
- Durante a chamada o túnel só é reiniciado se duas medições independentes concordarem que ele caiu, a autocura e os avisos de atualização esperam, e desligar a VPN ou trocar de servidor pede confirmação.
- Cada falha fica registrada com a causa provável (a VPN ou a sua internet) na linha do tempo da aba **Testes** e no arquivo `live-history.log`, e ao fim da chamada sai um resumo.
- Na aba **Servidores**, o botão de antena ao lado do ping (**Melhor servidor para live**) testa cada servidor com 20 pacotes e ordena pelo que mais importa para voz e vídeo: pouca oscilação e pouca perda.

Dica: quanto mais perto de você o servidor, melhor. Se só houver servidores distantes, coloque a **região do canal de voz** do Discord (Editar canal → Substituição de região) perto do servidor da VPN; assim o áudio não precisa ir e voltar.

Adicione programas por **Apps abertos**, por **Procurar programa** ou digitando o nome (ex.: `chrome`). As mudanças são salvas sozinhas. Se a VPN estiver ligada, clique em **Reconectar** para aplicá-las.

Com **Usar em todos os servidores** ligado, a mesma lista de programas vale para qualquer servidor, e você não precisa montá-la de novo ao trocar. Desligado, cada servidor guarda a sua própria lista.

**Rede de amigos (opcional):** uma rede local pela internet, no estilo do Radmin VPN, para jogar com amigos jogos que só funcionam "em LAN". Ela vem desligada e não tem relação com a VPN: ligar ou desligar a rede nunca mexe no túnel.

- Ligue em **Opções → Rede de amigos**. Aparece a aba **Rede**. Desligando ali, a aba some e nada fica rodando.
- **Entrar:** cole o convite que um amigo mandou em **Entrar com um convite**. Na primeira vez o aplicativo baixa o componente da rede (uns 30 MB) e confere o arquivo antes de usar.
- **Criar:** em **Criar uma rede**, dê um nome e informe o endereço do **nó de encontro** (veja abaixo). O convite já sai copiado para mandar aos amigos.
- Com a rede ligada, a aba mostra o seu IP na rede (10.126.126.x), quem está online, o ping e se a conexão é direta ou passa pelo nó. Clique em alguém para copiar o IP e usar no jogo.
- O interruptor ao lado do nome da rede liga e desliga na hora; o mesmo comando fica no menu do ícone perto do relógio. Desligada, o adaptador de rede e as regras de firewall somem.
- **Proteger este PC na rede** (vem ligado) bloqueia pastas compartilhadas e área de trabalho remota para quem está na rede; os jogos continuam funcionando.

O **nó de encontro** é um computador com endereço acessível pela internet que ajuda os PCs a se acharem; depois disso eles conversam direto, e o nó só repassa o tráfego quando a conexão direta não é possível. O convite já leva o endereço, então só quem cria a rede precisa saber dele. Como montar um está em [Para desenvolvedores](#como-funciona).

**Opções** (aba **Opções**):

| Opção | Para que serve |
|---|---|
| Rede de amigos (LAN virtual) | Mostra a aba **Rede** (veja acima). Vem desligada: nada é baixado nem fica rodando. |
| Reconectar se a conexão cair | Tenta voltar sozinho até 4 vezes (a primeira em 1 s), espera a rede voltar e, se o servidor continuar fora, troca para outro. Vem ligada. |
| Otimizar para chamadas, transmissões e jogos | Usa MTU 1380, que evita travamentos em tela compartilhada. Vem ligada. |
| Modo Estabilidade / Jogos UDP | Mantém o túnel ativo com sinal a cada 15 s e calibra o MTU antes de conectar. Vem ligado. |
| Modo Live (Discord) | Protege chamadas e transmissões do Discord contra reinícios, reparos e avisos no meio da conversa (veja acima). Vem ligado. |
| Autocura automática de rede e MTU | Limpa sozinho restos de conexões anteriores (bloqueio preso, DNS, rotas). Ajustes que exigem reconectar só são sugeridos: o aplicativo nunca reinicia sozinho um túnel que está funcionando. |
| Bloquear a internet se a VPN cair | Enquanto a VPN estiver ligada, nada sai do computador sem proteção. |
| Atalho global (Ctrl + Alt + V) | Liga ou desliga a VPN em qualquer momento, mesmo com outro app ou jogo em tela cheia. |
| Iniciar com o Windows | O aplicativo abre escondido perto do relógio. |
| Conectar ao abrir | Liga a VPN assim que o aplicativo abre. Junto com a opção anterior, você fica protegido desde que liga o computador. |
| Abrir sem pedir permissão | O Windows só pergunta na primeira vez. Vem ligada. |
| Verificar atualizações automaticamente | Avisa quando houver uma nova versão disponível e permite atualizar com 1 clique sem derrubar o túnel da VPN. |
| Fazer backup dos servidores (.zip) | Exporta todos os seus servidores `.conf` em um único arquivo `.zip` para guardar ou migrar de PC. |
| Liberar internet (reset do bloqueio) | Desativa as regras do driver do WireSock (kill switch) e restaura o acesso à internet imediatamente caso o túnel tenha travado. |

**Perto do relógio:** fechar a janela não fecha o aplicativo, ele continua no canto da barra de tarefas, perto do relógio. O ícone fica verde quando conectado, âmbar quando o servidor não responde e cinza quando desconectado. Clique com o botão direito no ícone para conectar, desconectar, conectar ao mais rápido, liberar a internet ou sair. No mesmo menu dá para trocar de servidor sem abrir a janela: os usados por último aparecem em **Recentes**, logo no menu, e a lista completa fica em **Todos os servidores**.

---

## Problemas comuns

| O que aparece | O que fazer |
|---|---|
| **"Sem resposta"** ou **"O servidor não respondeu"** | O servidor pode estar fora do ar, ou a sua rede pode bloquear VPN (comum em redes de empresa, escola e Wi-Fi público). Troque de servidor em **Servidores**. Se nenhum funcionar, teste em outra rede. |
| **"O arquivo deste servidor tem uma chave inválida"** | O arquivo foi alterado ou a chave foi cancelada no site do provedor. Baixe um arquivo novo. |
| **"WireSock não encontrado"**, ou o passo 1 não fica marcado | Clique em **Tentar de novo** no passo 1, ou instale pelo site (veja o passo 2). |
| **"O WireSock não conseguiu preparar a rede"** | Reinstale o WireSock e reinicie o computador. |
| O **IP mostrado continua o meu**, mesmo conectado | É normal no modo **Só estes**: o aplicativo mostra o IP dos programas fora da VPN. No modo **Todos**, deve aparecer o IP do servidor. Clique em ↻ ao lado de "Seu IP" para verificar de novo. |
| **Fiquei sem internet** | Se "Bloquear a internet se a VPN cair" estiver ligada e o servidor parar de responder, a internet fica bloqueada. Clique em **Liberar internet (reset do bloqueio)** na aba Opções ou no menu da bandeja perto do relógio. |
| O **navegador bloqueou o download** | Navegadores como Edge e Chrome bloqueiam downloads diretos de `.exe` sem assinatura. Baixe a versão em **`.zip`** (`VPN-Panel-Setup-<versão>.zip`), extraia e execute o instalador. |
| O **antivírus** reclamou do instalador ou do Controle Inteligente | Acontece com aplicativos sem assinatura digital paga. Se o Windows bloquear, clique com o botão direito no arquivo baixado → **Propriedades** → marque a caixinha **Desbloquear** no rodapé e clique em **OK**. |
| Quero ver o que aconteceu | **Opções → Ver log da conexão** (falhas críticas também são gravadas em `crash.log`). |

## Desinstalar

**Configurações → Aplicativos → Aplicativos instalados → VPN Panel → Desinstalar.**

A VPN é desconectada e o aplicativo é removido. Os seus servidores e configurações ficam guardados, para o caso de você instalar de novo, a não ser que você ligue **"Apagar meus servidores e configurações"**. O WireSock é um programa separado e pode ser desinstalado do mesmo jeito.

## Sem instalar (versão portátil)

Na [página de downloads](https://github.com/JMqz/VPN-Panel/releases/latest) também tem o **`VpnPanel-<versão>-portatil.exe`** (e em **`.zip`**). É o mesmo aplicativo, sem instalador e sem atalho no menu Iniciar. Basta abrir.

---

## Para desenvolvedores

### Onde ficam as coisas

| Caminho | O que é |
|---|---|
| `src\` | Código-fonte: um arquivo `.cs` por classe (`Tunnel.cs`, `ConfFile.cs`, `NetDiag.cs`...), a janela principal dividida por área em `MainWindow.*.cs`, mais `MainWindow.xaml`, `Theme.xaml`, `app.manifest`, `setup.manifest` e `build.ps1` |
| `C:\Program Files\VPN Panel\VpnPanel.exe` | Aplicativo instalado (pasta que só administradores alteram) |
| `%APPDATA%\VPN Panel\configs\*.conf` | Servidores importados (contêm chaves privadas) |
| `%APPDATA%\VPN Panel\settings.json` | Servidor ativo e opções |
| `%APPDATA%\VPN Panel\locations.json` | País e cidade de cada servidor, guardados para não consultar de novo |
| `%APPDATA%\VPN Panel\wiresock.log` | Registro do WireSock na última conexão |
| `%APPDATA%\VPN Panel\live-history.log` | Histórico das chamadas do Discord: início, falhas do túnel com a causa provável e resumo |
| `C:\Program Files\VPN Panel\lan\` | Componente da Rede de amigos (EasyTier), baixado só quando a rede é ligada pela primeira vez. Na versão portátil fica em `%APPDATA%\VPN Panel\lan\` |
| `%APPDATA%\VPN Panel\lan.log` | Registro do componente da Rede de amigos na última vez em que foi ligada |

### Compilar

```
powershell -NoProfile -ExecutionPolicy Bypass -File src\build.ps1
```

Gera `dist\VpnPanel.exe` e `dist\VPN Panel Setup.exe` usando o compilador `csc.exe` que já vem no Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`, .NET Framework 4.8), sem instalar nada.

- O compilador é C# 5, então não use sintaxe nova (`$"..."`, `?.`, membros com `=>`).
- O instalador usa o mesmo código, com outro ponto de entrada (`VpnPanel.SetupProgram`), o aplicativo embutido e um manifesto que exige administrador.
- As telas (XAML) e o ícone ficam embutidos e são carregados com `XamlReader`. Os eventos são ligados no código pelo `x:Name`.
- Para atualizar uma cópia instalada, rode o instalador novo ("Reinstalar" ou "Atualizar").

### Como funciona

- **Permissão de administrador:** o executável abre sem privilégios (`asInvoker`).
  - Se a tarefa agendada **VPN Panel - Abrir** existir e apontar para ele, ele a executa pelo Agendador de Tarefas (API COM), e o aplicativo abre com privilégio máximo, sem o aviso do Windows.
  - Se não, ele se reabre pedindo permissão (`runas`).
  - A cópia com privilégio cria ou atualiza essa tarefa. Desligar "Abrir sem pedir permissão" remove a tarefa.
- **Iniciar com o Windows:** tarefa **VPN Panel**, que roda 10 s depois de você entrar no Windows (`VpnPanel.exe --tray`).
- **Instalar o WireSock:** `winget install --id NTKERNEL.WireSockVPNClientCLI --exact --source winget --silent`. Sem winget, baixa o instalador mais recente de wiresock.net, confere a assinatura digital (válida e da The Anti-Cloud Corporation / NT Kernel) e roda com `/S`.
- **Conectar:** inicia `wiresock-client.exe run -config <arquivo> -log-level info [-network-lock enabled]` direto com `CreateProcessW`, sem `cmd.exe`. A saída vai para o `wiresock.log`, cujo handle é o único herdado pelo processo (`STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`). Assim o túnel continua ativo mesmo se o aplicativo fechar, e uma nova cópia do aplicativo reconhece o túnel que já está rodando.
- **Desconectar:** encerra o processo de forma assíncrona em segundo plano (enviando Ctrl+C e aguardando até 3 s) sem congelar a interface. Se o bloqueio de internet estava ligado, o aplicativo roda `wiresock-client reset-network-lock` automaticamente. O usuário também pode acionar o reset manual a qualquer momento pelo botão "Liberar internet".
- **Latência:** ping (ICMP) até o endereço de cada servidor, medido só sob demanda na aba **Servidores**, no máximo 4 ao mesmo tempo, e guardado apenas na memória.
- **Velocidade:** lê os contadores de bytes da placa de rede física (`GetIfEntry2` / `MIB_IF_ROW2`) uma vez por segundo, só com a janela visível. O total da sessão vem da diferença em relação ao valor no momento da conexão.
- **Status:** "Conectado" exige a confirmação do servidor (o *handshake* do WireGuard). O aplicativo acompanha o `wiresock.log` procurando as linhas `Handshake initiation sent` (pedido enviado) e `Handshake response received` (servidor respondeu).
  - **15 s** sem resposta: mostra "Sem resposta".
  - **60 s** sem resposta: se a conexão já tinha funcionado, reconecta; se nunca funcionou, derruba o túnel e avisa. Assim o bloqueio de internet nunca fica ativo sem a pessoa saber.
  - O WireSock grava o log em blocos, então uma linha pode demorar a aparecer. Por isso o aplicativo também pergunta ao próprio túnel: se nenhum *handshake* aparecer no log, uma consulta DNS (UDP) enviada por dentro do túnel confirma a conexão em vez de ela ser presumida.
  - No modo **Só estes**, essa consulta se repete a cada 10 s pelo `VpnPanelProbe.exe` (uma cópia do aplicativo incluída na lista de programas da VPN). Três falhas seguidas, com a internet direta funcionando, reconectam o túnel em cerca de 25 s. Sem internet direta nada é feito, porque reconectar não resolveria.
  - Se o WireSock fechar em menos de 10 s, conta como falha, e o aviso traduz o erro registrado.
  - Túneis iniciados fora do aplicativo não têm registro para conferir e aparecem como conectados.
- **Modo Live (Discord):** a chamada é detectada lendo a tabela de sockets UDP do Windows (`GetExtendedUdpTable`): o motor de voz e vídeo do Discord abre um socket UDP por conexão de mídia em um processo que não é o serviço de rede do Chromium. Nada é injetado no Discord e nenhum pacote é capturado. Com a chamada ativa, uma consulta DNS (UDP) cruza o túnel a cada 2 ou 3 s por um segundo `VpnPanelProbe.exe`, separado do que confere se o túnel está vivo; uma consulta perdida é comparada com a internet direta para apontar a causa. Se a conferência de vida acusar queda, mas essa medição tiver respondido nos últimos 6 s, o túnel não é reiniciado.
- **Programas na VPN:** grava `AllowedApps` (só estes) ou `DisallowedApps` (todos, exceto) na seção `[Peer]` do `.conf` e mantém todas as outras linhas idênticas, byte a byte. Segundo relatos no fórum do WireSock, `DisallowedApps` falhou em algumas versões.
- **Atualização:** o instalador baixado só é executado se o SHA-256 dele for igual ao que o GitHub publica para o arquivo da release, e fica travado contra troca entre a conferência e a execução.
- **Motor:** no modo **Todos**, se o WireGuard oficial estiver instalado, o túnel sobe como serviço do WireGuard (Wintun); nos modos por programa, sempre pelo WireSock. A aba **Testes** mostra diagnósticos e a linha do tempo de eventos da rede.
- **Rede de amigos:** o motor é o [EasyTier](https://github.com/EasyTier/EasyTier) (LGPL-3.0), usado como programa separado, sem alterações.
  - **Instalação:** o zip da release 2.6.4 é baixado do GitHub do EasyTier e só é extraído se o SHA-256 for igual ao fixado em `Lan.cs`. Os executáveis do EasyTier não têm assinatura digital, por isso a conferência é pelo hash. Só entram `easytier-core.exe`, `easytier-cli.exe`, `wintun.dll` e `Packet.dll`.
  - **Ligar:** inicia `easytier-core --network-name <id> -d -p <nó> --hostname <nome> --dev-name "VPN Panel LAN"` com portas de escuta aleatórias e o RPC só em `127.0.0.1`. A senha da rede vai pela variável de ambiente `ET_NETWORK_SECRET`, nunca pela linha de comando. Como o túnel, a rede continua ligada se o aplicativo fechar, e a próxima cópia a reconhece.
  - **Quem está online:** `easytier-cli -o json peer`, só com a aba **Rede** aberta. Na bandeja o aplicativo apenas confere se o processo está vivo.
  - **Desligar:** Ctrl+C no processo (o adaptador some) e remoção das regras que o EasyTier cria no Firewall do Windows e nunca apaga.
  - **Convite:** `VPL1-` seguido de um JSON em base64 com o nome, o identificador (`vp-...`), a senha e os nós de encontro. Quem tem o convite entra na rede, então trate-o como uma senha.
  - **Nó de encontro:** qualquer `easytier-core` alcançável pela internet serve, por exemplo `easytier-core --network-name meu-no --network-secret <outra senha> -l udp://0.0.0.0:11010 tcp://0.0.0.0:11010 --relay-network-whitelist "vp-*"` em um Linux, sem root. Sem IP público, um túnel UDP (playit.gg e similares) apontando para a porta 11010 resolve. O EasyTier não tem mais nó público oficial, por isso o aplicativo não traz nenhum embutido.
  - **Com a VPN ligada:** no modo **Só estes** a rede de amigos usa a internet normal. No modo **Todos** o tráfego dela passa por dentro da VPN (funciona, com ping maior); em **Todos, exceto**, adicione `easytier-core` à lista.
- **Uma janela só:** o mutex `Global\VpnPanelWireSock` impede uma segunda cópia. Abrir o aplicativo de novo sinaliza `Global\VpnPanelWireSockShow`, e a janela que já está aberta aparece.
- **Desinstalar:** `VpnPanel.exe --uninstall` remove as tarefas, os atalhos, a chave `HKLM\...\Uninstall\VPN Panel` e os arquivos em Arquivos de Programas. O próprio `VpnPanel.exe` em uso e a pasta vazia são removidos pelo Windows na próxima reinicialização (`MoveFileEx` com `MOVEFILE_DELAY_UNTIL_REBOOT`).

### Releases e assinatura

As releases são compiladas pelo GitHub Actions (`.github/workflows/release.yml`) a partir de uma tag `v*`. O aplicativo é assinado primeiro, depois o instalador é montado com o aplicativo já assinado dentro, e por fim o instalador é assinado.

### Próximos passos

- Rodar como serviço do Windows, para conectar antes de entrar na conta

---

## Política de assinatura de código

Assinatura de código gratuita fornecida por [SignPath.io](https://about.signpath.io), certificado da [SignPath Foundation](https://signpath.org).

- **Autores e revisores (committers e reviewers):** [JMqz](https://github.com/JMqz)
- **Aprovadores (approvers):** [JMqz](https://github.com/JMqz)

Só são assinados executáveis compilados pelo GitHub Actions a partir do código público deste repositório.

## Privacidade

O VPN Panel não coleta nem envia dados pessoais para o autor. Ele só se comunica com terceiros nestes casos:

- **ipinfo.io:** para mostrar o seu IP e a localização (aba Início) e o país de cada servidor.
- **winget / wiresock.net:** para baixar e instalar o WireSock, quando ele ainda não está instalado.
- **Endereço dos servidores VPN:** ping (ICMP) para medir a latência, só quando a aba Servidores está aberta.
- **O provedor de VPN escolhido por você**, através do WireSock, quando você conecta.
- **Rede de amigos (só se você ligar):** o GitHub, para baixar o EasyTier; o nó de encontro que está no convite, que vê o endereço IP de cada participante; e os próprios participantes, com quem o seu PC se conecta direto. O EasyTier também consulta servidores STUN públicos para descobrir o seu endereço de internet.

Servidores, chaves e configurações ficam apenas no seu computador, em `%APPDATA%\VPN Panel`.

## Licença

[MIT](LICENSE)
