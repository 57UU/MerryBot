const list=document.querySelector("#app > div:nth-child(1) > div:nth-child(2) > div:nth-child(3) > div > div").childNodes

let newNode=document.createElement("div")
for(let i=1;i<=20;i++){
    const entry=list[i]
    newNode.appendChild(entry.cloneNode(true))
}
document.body.innerHTML=newNode.innerHTML
